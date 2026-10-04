"""Explicit-path CLI for the durable source/dependency census."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from layout_import import cdda, db


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    hybrid=commands.add_parser("hybrid",help="populate vacant CDDA field parcels with seeded PZ house layouts")
    for flag in ("district","source","zomboid-catalogue","db","catalogue","decor-db","out"):
        hybrid.add_argument('--'+flag,type=Path,required=True)
    hybrid.add_argument("--seed",required=True)
    hybrid.add_argument("--count",type=int,default=1)
    hybrid.add_argument("--name",required=True)
    combined=commands.add_parser("combine",help="place a validated building into a district vacant parcel")
    combined.add_argument("--district",type=Path,required=True)
    combined.add_argument("--building",type=Path,required=True)
    combined.add_argument("--offset",type=int,nargs=2,required=True)
    combined.add_argument("--name",required=True)
    combined.add_argument("--out",type=Path,required=True)
    pz_scan = commands.add_parser("zomboid-scan",help="index authored buildings in a local B42 map")
    pz_scan.add_argument("--source",type=Path,required=True)
    pz_scan.add_argument("--map",default="Muldraugh, KY")
    pz_scan.add_argument("--out",type=Path,required=True)
    pz = commands.add_parser("zomboid-resolve",help="adapt authored PZ edge geometry to shared UO layout semantics")
    pz.add_argument("--source",type=Path,required=True)
    pz.add_argument("--map",default="Muldraugh, KY")
    pz.add_argument("--header",required=True)
    pz.add_argument("--building",type=int,required=True)
    pz.add_argument("--name",required=True)
    pz.add_argument("--out",type=Path,required=True)
    pz.add_argument("--db",type=Path,required=True)
    scan = commands.add_parser("scan", help="read source JSON into an immutable snapshot")
    scan.add_argument("--source", type=Path, required=True)
    scan.add_argument("--db", type=Path, required=True)
    scan.add_argument("--version-claim", help="unverified caller claim, never a clean-commit assertion")
    scan.add_argument("--mods", nargs="*", default=[], help="ordered mod IDs; core dda always included")
    deps = commands.add_parser("dependencies", help="create an explicit mod-profile dependency census")
    deps.add_argument("--db", type=Path, required=True)
    deps.add_argument("--snapshot", required=True)
    deps.add_argument("--mods", nargs="*", default=[])
    report = commands.add_parser("coverage", help="report every definition's current disposition")
    report.add_argument("--db", type=Path, required=True)
    report.add_argument("--profile", required=True)
    report.add_argument("--out", type=Path)
    resolve = commands.add_parser("resolve", help="resolve an engine-baked footprint into semantic levels and rooms")
    resolve.add_argument("--db", type=Path, required=True)
    resolve.add_argument("--profile", required=True)
    resolve.add_argument("--bake", type=Path, required=True)
    resolve.add_argument("--bounds", type=int, nargs=4, required=True, metavar=("X0", "Y0", "X1", "Y1"))
    resolve.add_argument("--levels", type=int, nargs="+", default=[0, 1])
    resolve.add_argument("--name", required=True)
    resolve.add_argument("--rotate", type=int, choices=[0, 1, 2, 3], default=0)
    resolve.add_argument("--out", type=Path, required=True)
    build = commands.add_parser("build", help="assemble semantic geometry with native mined UO families")
    build.add_argument("--db", type=Path, required=True)
    build.add_argument("--profile", required=True)
    build.add_argument("--layout", type=Path, required=True)
    build.add_argument("--catalogue", type=Path, required=True)
    build.add_argument("--decor-db", type=Path, required=True)
    build.add_argument("--theme", type=Path)
    build.add_argument("--out", type=Path, required=True)
    build.add_argument("--scene",action="store_true",help="partition large footprints into disjoint native multis")
    usage_parser = commands.add_parser("usage", help="append one immutable usage event")
    usage_parser.add_argument("--db", type=Path, required=True)
    usage_parser.add_argument("--event", type=Path, required=True)
    district = commands.add_parser("district", help="compose baked city parcels as a UO world project and native multi scene")
    district.add_argument("--db", type=Path, required=True)
    district.add_argument("--profile", required=True)
    district.add_argument("--bake", type=Path, required=True)
    district.add_argument("--bounds", type=int, nargs=4, required=True)
    district.add_argument("--origin", type=int, nargs=2, required=True)
    district.add_argument("--name", required=True)
    district.add_argument("--catalogue", type=Path, required=True)
    district.add_argument("--decor-db", type=Path, required=True)
    district.add_argument("--theme", type=Path)
    district.add_argument("--out", type=Path, required=True)
    for command_name in ("district-stage","district-prove"):
        p = commands.add_parser(command_name)
        p.add_argument("--built",type=Path,required=True)
        p.add_argument("--stage",type=Path,required=True)
        if command_name == "district-prove":
            p.add_argument("--out",type=Path,required=True)
            p.add_argument("--clip",type=Path)
            p.add_argument("--only-part",help="prove one scene part's room/stair tour; all scene parts still load")
    evidence = commands.add_parser("evidence",help="persist native/editor/gameplay reports with content hashes")
    evidence.add_argument("--db",type=Path,required=True)
    evidence.add_argument("--build",required=True)
    evidence.add_argument("--kind",choices=["native-readback","editor","gameplay","negative-blocker","roof-hide"],required=True)
    evidence.add_argument("--report",type=Path,required=True)
    evidence.add_argument("--artifacts",type=Path,nargs="*",default=[])
    engine=commands.add_parser("engine-bake",help="run isolated headless planning/detailed export with verified snapshot inputs")
    engine.add_argument("--db",type=Path,required=True)
    engine.add_argument("--profile",required=True)
    engine.add_argument("--source",type=Path,required=True)
    engine.add_argument("--binary",type=Path,required=True)
    engine.add_argument("--seed",required=True)
    engine.add_argument("--bounds",type=int,nargs=4,required=True)
    engine.add_argument("--world-size",type=int,choices=range(1,11))
    engine.add_argument("--timeout",type=int,default=900)
    engine.add_argument("--out",type=Path,required=True)
    args = parser.parse_args(argv)
    try:
        from guo import load_config
        retail=load_config().client_data.resolve()
        for flag in ('db','out','stage','clip'):
            value=getattr(args,flag,None)
            if value and value.resolve().is_relative_to(retail):
                raise ValueError(f"{flag} output is inside the retail installation")
        if getattr(args,"db",None) and getattr(args,"source",None) and args.db.resolve().is_relative_to(args.source.resolve()):
            raise ValueError("catalogue must be outside the source installation")
        if args.command=="hybrid":
            if not 1<=args.count<=128:
                raise ValueError("hybrid count must be 1..128")
            from guo import load_config
            from layout_import.hybrid import populate
            print(json.dumps(populate(load_config(),args.district,args.source,args.zomboid_catalogue,args.db,
                args.catalogue,args.decor_db,args.seed,args.count,args.name,args.out),indent=2))
            return 0
        if args.command=="combine":
            from guo import load_config
            from layout_import.combine import combine
            print(json.dumps(combine(load_config(),args.district,args.building,args.offset,args.name,args.out),indent=2))
            return 0
        if args.command in ("zomboid-scan","zomboid-resolve"):
            from layout_import import zomboid, catalogue, semantic
            if args.out.resolve().is_relative_to(args.source.resolve()) or (hasattr(args,"db") and args.db.resolve().is_relative_to(args.source.resolve())):
                raise ValueError("derived output must be outside the source installation")
            if args.command == "zomboid-scan":
                result=zomboid.scan(args.source,args.out,args.map)
                print(json.dumps({"buildings":len(result["buildings"]),"problems":result["problems"]}))
                return 0 if not result["problems"] else 1
            layout=zomboid.resolve(args.source,args.map,args.header,args.building,args.name)
            con=db.open_db(args.db)
            try:
                provenance=layout["provenance"]
                implementation=cdda.digest(Path(zomboid.__file__).read_text(encoding="utf-8"))
                snapshot=cdda.digest([provenance,implementation])
                profile=cdda.digest(["zomboid-core",snapshot])
                with con:
                    con.execute("INSERT OR IGNORE INTO source_snapshot VALUES(?,?,?,?,?,?,?,?)",
                        (snapshot,"project-zomboid","b42-v1",None,"local-installed-data",provenance["license"],
                         cdda.canonical(provenance),cdda.canonical({"adapter":implementation})))
                    con.execute("INSERT OR IGNORE INTO profile VALUES(?,?,?,?,?,?)",(profile,snapshot,"[]",'["zomboid-core"]',"resolved","[]"))
                layout["instance_id"]=catalogue.save_layout(con,profile,layout)
                layout["profile_id"]=profile
                args.out.mkdir(parents=True,exist_ok=True)
                (args.out/"layout.json").write_text(json.dumps(layout,indent=1),encoding="utf-8")
                semantic.plans(layout,args.out)
                print(json.dumps({"profile":profile,"instance":layout["instance_id"],"rooms":[len(l["rooms"]) for l in layout["levels"]],"coverage":layout["source_coverage"],"blocked":layout.get("blocked")},indent=2))
                return 1 if layout.get("blocked") else 0
            finally:
                con.close()
        if args.command in ("district-stage","district-prove"):
            from guo import load_config
            from layout_import import deploy
            cfg = load_config()
            if args.command == "district-stage":
                return deploy.district_stage(cfg,args.built,args.stage)
            return deploy.district_prove(cfg,args.built,args.stage,args.out,args.clip,args.only_part)
        if getattr(args, "out", None) and args.out.resolve() == args.db.resolve():
            raise ValueError("coverage output cannot overwrite the catalogue")
        if args.command != "scan" and not args.db.is_file():
            raise ValueError("catalogue does not exist; run scan first")
        if args.command == "scan":
            # The scanner never writes inside the source checkout.
            if args.db.resolve().is_relative_to(args.source.resolve()):
                raise ValueError("output database must be outside the source checkout")
        con = db.open_db(args.db)
        try:
            if args.command == "engine-bake":
                from guo import load_config
                from layout_import.engine import export
                cfg=load_config()
                if args.out.resolve().is_relative_to(cfg.client_data.resolve()):
                    raise ValueError("engine output is inside retail install")
                print(json.dumps(export(con,args.profile,args.source,args.binary,args.seed,args.bounds,args.out,args.timeout,args.world_size),indent=2))
                return 0
            if args.command == "evidence":
                from layout_import.deploy import record_evidence
                ok = record_evidence(con,args.build,args.kind,args.report,args.artifacts)
                print(json.dumps({"build":args.build,"kind":args.kind,"passed":ok}))
                return 0 if ok else 1
            if args.command in ("resolve", "build", "district"):
                from guo import load_config
                cfg = load_config()
                if args.out.resolve().is_relative_to(cfg.client_data.resolve()):
                    raise ValueError("output is inside the retail install")
                from layout_import import catalogue, semantic
                if args.command == "district":
                    from layout_import.district import build
                    result = build(cfg, con, args.profile, args.bake, args.bounds, args.name,
                                   args.origin, args.out, args.catalogue, args.decor_db,
                                   json.loads(args.theme.read_text(encoding="utf-8")) if args.theme else None)
                    print(json.dumps({"name":result["name"],"status":result["status"],
                                      "parcels":len(result["parcels"]),"problems":result["problems"]},indent=2))
                    return 0 if result["status"] == "native-valid" else 1
                if args.command == "resolve":
                    from layout_import.bake import Bake
                    source = semantic.SourceDefinitions(con, args.profile)
                    layout = Bake(args.bake).layout(source, args.name, args.bounds, sorted(set(args.levels)))
                    if args.rotate:
                        layout = semantic.rotate(layout, args.rotate)
                    instance = catalogue.save_layout(con, args.profile, layout)
                    layout["instance_id"] = instance
                    args.out.mkdir(parents=True, exist_ok=True)
                    (args.out / "layout.json").write_text(json.dumps(layout, indent=1, ensure_ascii=False), encoding="utf-8")
                    semantic.plans(layout, args.out)
                    print(json.dumps({"instance": instance, "layout_hash": layout["layout_hash"], "levels": [
                        {"z": level["z"], "rooms": len(level["rooms"]), "openings": len(level["openings"])} for level in layout["levels"]],
                        "out": str(args.out)}, indent=2))
                    return 0
                from layout_import import native
                layout = json.loads(args.layout.read_text(encoding="utf-8"))
                if layout.get("blocked"):
                    raise ValueError(layout["blocked"])
                instance = catalogue.save_layout(con, args.profile, layout)
                theme = json.loads(args.theme.read_text(encoding="utf-8")) if args.theme else native.DEFAULT_THEME
                lib = native.furniture_library(args.decor_db, cfg.client_data)
                cat = native.G.Catalogue(args.catalogue)
                try:
                    comps, side, record = native.compile_layout(layout, cat, lib, theme,as_scene=args.scene)
                except semantic.ResolutionError as exc:
                    record={"format":1,"name":layout["name"],"layout_hash":layout["layout_hash"],"theme":theme,
                            "status":"blocked","problems":[str(exc)],"proof_status":"not-run"}
                    converter=cdda.digest(Path(native.__file__).read_text(encoding="utf-8"))
                    build_id=catalogue.save_build(con,instance,theme,converter,record)
                    catalogue.save_validation(con,build_id,"native-offline",{"passed":False,"problems":record["problems"]})
                    args.out.mkdir(parents=True,exist_ok=True)
                    (args.out/"layout-build.json").write_text(json.dumps(record,indent=1),encoding="utf-8")
                    print(json.dumps({"build":build_id,**record},indent=2))
                    return 1
                ok = (native.save_native_scene if args.scene else native.save_native)(comps, side, record, args.out, cfg.client_data)
                from layout_import.engine import file_hash
                inputs={"implementation":{path.name:file_hash(path) for path in Path(__file__).parent.glob('*.py') if not path.name.startswith('test_')},
                    "native_families":{path.name:file_hash(path) for path in args.catalogue.glob('*.json')},
                    "decor_catalogue":file_hash(args.decor_db)}
                record['converter_inputs']=inputs
                converter = cdda.digest(inputs)
                build_id = catalogue.save_build(con, instance, theme, converter, record)
                catalogue.save_validation(con, build_id, "native-offline", {"passed": ok, "problems": record["problems"]})
                files=["scene.json","layout-build.json","preview.png"] if args.scene else ["components.json","multi.json","layout-build.json","preview.png"]
                catalogue.save_artifacts(con,build_id,"native-build",[args.out/file for file in files]+(list((args.out/'parts').glob('*.json')) if args.scene else []))
                (args.out / "catalogue-build.json").write_text(json.dumps({"build_id": build_id, "instance_id": instance}), encoding="utf-8")
                print(json.dumps({"build": build_id, "components": len(comps), "doors": len(side["doors"]),
                                  "status": record["status"], "problems": record["problems"], "out": str(args.out)}, indent=2))
                return 0 if ok else 1
            if args.command == "usage":
                from layout_import.catalogue import usage
                event = json.loads(args.event.read_text(encoding="utf-8"))
                usage(con, event)
                print(json.dumps({"event": event["id"], "status": "recorded-idempotently"}))
                return 0
            if args.command == "scan":
                snapshot = cdda.scan(con, args.source, args.version_claim)
                profile = cdda.make_profile(con, snapshot, args.mods)
            elif args.command == "dependencies":
                profile = cdda.make_profile(con, args.snapshot, args.mods)
            else:
                profile = args.profile
            result = cdda.coverage(con, profile)
            text = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
            if getattr(args, "out", None):
                args.out.parent.mkdir(parents=True, exist_ok=True)
                args.out.write_text(text, encoding="utf-8")
            print(text, end="")
            return 1 if result["profile_status"] == "blocked" else 0
        finally:
            con.close()
    except (ValueError, OSError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
