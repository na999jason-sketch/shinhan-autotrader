"""Reject a build that cannot require the Windows 32-bit CLR for Indi OCX."""
import struct
import sys
from pathlib import Path


def verify(path):
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
    verify(sys.argv[1])
