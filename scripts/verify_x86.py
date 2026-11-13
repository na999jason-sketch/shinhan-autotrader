"""Reject a build that cannot require the Windows 32-bit CLR for Indi OCX."""
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def verify(path, require_admin=False):
    data = Path(path).read_bytes()
    pe_offset = struct.unpack_from('<I', data, 0x3C)[0]
    if data[pe_offset:pe_offset + 4] != b'PE\0\0':
        raise ValueError('Not a PE executable')
    machine, sections = struct.unpack_from('<HH', data, pe_offset + 4)
    if machine != 0x14C:
        raise ValueError('Expected I386 PE machine, got ' + hex(machine))
    optional_size = struct.unpack_from('<H', data, pe_offset + 20)[0]
    optional = pe_offset + 24
    if struct.unpack_from('<H', data, optional)[0] != 0x10B:
        raise ValueError('Expected PE32 optional header')
    cli_rva = struct.unpack_from('<I', data, optional + 96 + 14 * 8)[0]
    section_table = optional + optional_size
    def file_offset(rva):
        for number in range(sections):
            location = section_table + number * 40
            virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from('<IIII', data, location + 8)
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return raw_offset + rva - virtual_address
        raise ValueError('PE RVA is outside its sections')

    if require_admin:
        resource_rva = struct.unpack_from('<I', data, optional + 96 + 2 * 8)[0]
        resource = file_offset(resource_rva)

        def entries(relative):
            directory = resource + relative
            named, ids = struct.unpack_from('<HH', data, directory + 12)
            return [struct.unpack_from('<II', data, directory + 16 + number * 8)
                    for number in range(named + ids)]

        manifest_type = next((value for name, value in entries(0) if name == 24), None)
        if manifest_type is None or not manifest_type & 0x80000000:
            raise ValueError('RT_MANIFEST resource is missing')
        level = entries(manifest_type & 0x7fffffff)
        if not level or not level[0][1] & 0x80000000:
            raise ValueError('Manifest language directory is missing')
        language = entries(level[0][1] & 0x7fffffff)
        if not language or language[0][1] & 0x80000000:
            raise ValueError('Manifest data entry is missing')
        rva, size = struct.unpack_from('<II', data, resource + language[0][1])
        start = file_offset(rva)
        manifest = ET.fromstring(data[start:start + size])
        execution = manifest.find('.//{urn:schemas-microsoft-com:asm.v3}requestedExecutionLevel')
        if execution is None or execution.get('level') != 'requireAdministrator' or execution.get('uiAccess') != 'false':
            raise ValueError('Indi IPC requires the embedded requireAdministrator/uiAccess=false manifest')
        print('PASS: embedded manifest requires Administrator for Indi WM_COPYDATA IPC')
    for index in range(sections):
        section = section_table + index * 40
        virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from('<IIII', data, section + 8)
        if virtual_address <= cli_rva < virtual_address + max(virtual_size, raw_size):
            cli = raw_offset + cli_rva - virtual_address
            flags = struct.unpack_from('<I', data, cli + 16)[0]
            if not flags & 0x2:
                raise ValueError('CLR 32BITREQUIRED is missing; OCX must run x86')
            print('PASS: I386 PE32, CLR 32BITREQUIRED; ' + str(path))
            return
    raise ValueError('CLR header not found')


if __name__ == '__main__':
    verify(sys.argv[1], require_admin='--require-admin' in sys.argv[2:])
