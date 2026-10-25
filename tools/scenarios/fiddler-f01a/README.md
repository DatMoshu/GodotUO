# F01a synthetic fixture recipe

Layer: Tools; toggle: none; doctor:
`launchers/dev/fiddler_parity_doctor.bat --build` (or `.sh`).
Docs: `tools/fiddler_parity/README.md`; no GUI/private shard/proprietary data.

The runtime scenario schema does not execute independent tool CLIs. This is
the reproducible fixture specification permitted by the F01a brief:

1. Run `python -m pytest tools/fiddler_parity/proof_static.py -q --basetemp <dedicated-scratch>`.
2. For each MUL/IDX and uncompressed UOP layout, construct one occupied static
   and one free ID. Records have a 0x12345678 unknown header, two rows of three
   pixels, explicit row offsets and trailing padding. Expected RGBA literals
   cover red, blue, green, white, transparency and near-black.
3. Prove direct upstream decode of the occupied record before exercising the
   existing GUO writer's free-slot insertion. Verify expected payload SHA256,
   dimensions/pixels, untouched records and unchanged inputs.
4. In separate scratch copies, change a pixel, choose the wrong ID, corrupt a
   container offset, truncate a payload, change an untouched record, change the
   unknown header and corrupt a row offset. Every verdict must fail.
5. Exercise missing-oracle, excessive manifest record count, output overlap and
   unsupported-compression paths. None may pass by skipping.

Retain reports and test logs under the shared GUO evidence run. This recipe
does not certify an editor UI, occupied-record replacement or other domains.
