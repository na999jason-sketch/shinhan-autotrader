# Native COM event integration fixture

This standalone Windows x86 test links the production `ComEventSubscriptions.cs` helper and uses a synthetic C++ COM object and .NET Framework 4.8 RCW. It makes real `IConnectionPoint::Advise`, outgoing-interface `QueryInterface`, and `IDispatch::Invoke` calls through `ComEventsHelper`; it is not a managed fake transport.

Run from a Windows Visual Studio 2022 developer PowerShell or the GitHub Windows runner after `microsoft/setup-msbuild`:

```powershell
./tests/native-com/run.ps1
```

The report is written to `reports/native-com-tests.txt`. The test checks outgoing DISPIDs distinct from Indi's, scalar `VT_I4` and `VT_I2` callbacks, STA execution, a native receive buffer changing after a synchronous reentrant `RequestData`, subscription rooting across GC, exact detach, reconnect, rejected bindings, and native object release. The fixture does not register a COM class and does not ship with the production EXE.

This proves the production subscription helper's .NET/native connection-point mechanism under the fixture's contract. It does not verify the actual Indi OCX, `AxHost` activation, login/server readiness, actual field definitions or `ReceiveData` delivery from Shinhan. A positive synthetic request ID is not evidence that a broker request succeeded.

The typed-dispatch fixture also exercises the production read-only adapter with the manual `TR_1110_11` snapshot query, short-code input at index0, and price output at index24. It deliberately supplies no SC symbol echo for that snapshot, keeps SC polling separate, and verifies that new-order and cancellation queries never reach native dispatch. The official resource contract is recorded in `docs/INDI_QUERY_CONTRACT.md`; the fixture does not validate broker values.

The RT regression uses Indi's `ReceiveRTData` DISPID2 and a formal VARIANT carrying the `SC` BSTR. The callback synchronously reads fields1/2/3/7/17 through the production typed getter. It preserves the full eight-digit clock `10045512`, matching the numeric payload format reported from the user's installed Indi instance. The synthetic field buffer and other values change immediately after the callback, proving that delayed reads would lose the captured RT fields. Collection and exact detach are tested on this path as well. The fixture does not connect to an Indi server or independently verify the payload format's semantics.
