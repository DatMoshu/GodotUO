# paging_tests

The engine-free core of L4 map paging (`godot/GUO/src/Labs/Paging`: the page
manifest, the file verifier and the residency session), compiled on its own
and run with dotnet. No engine, no client data, no shard. See
[the paging README](../../godot/GUO/src/Labs/Paging/README.md) and ADR-0046.

```
dotnet run --project tools\paging_tests\PagingTests.csproj -c Release
dotnet run --project tools\paging_tests\PagingTests.csproj -- --validate <pages.json>
dotnet run --project tools\paging_tests\PagingTests.csproj -- --fixture <dir> [--size WxH]
```

The smoke runs the tests; `launchers\dev\paging_doctor` runs all three.
`--fixture` writes a synthetic two-page pack (flat land, facets 32 and 33);
keep it outside the checkout.
