# atomic_write_tests

SF8: the world project writers (`WorldProject`, `ShardObjects`, `AssetOverlay`) save through
`Workspace.WriteAtomic`: a temporary file in the target's folder, then a move over the target. This checks that an
interrupted write and a failed save leave the old file whole and leave no temporary behind, and that the text
overload writes the same bytes as `File.WriteAllText`.

```
dotnet run --project tools/atomic_write_tests/AtomicWriteTests.csproj
```
