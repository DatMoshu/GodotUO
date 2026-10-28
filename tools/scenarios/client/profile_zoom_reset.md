# Saved zoom resets to the standard maximum

Owner decision, 2026-10-27: when leaving a session or reading a saved profile,
zoom above the standard 2.5 maximum must become 2.5. This repairs the existing
wide-zoom extension; no new option or profile field is introduced. Values at
or below 2.5 retain their exact value. Explicit --zoom-max still permits wide
zoom during the current session.

Profile's JSON callbacks clamp DefaultScale before serialization and after
deserialization. The shared save path covers GameScene.Unload (logout/normal
exit), GameController.SaveOnPause, Options saves and default-profile saves.
An older file left behind by a killed process is clamped when read, before
GameScene.Load initializes the camera. A force-kill cannot run exit cleanup;
read-time clamping is the recovery boundary. Saving does not change Camera.Zoom.

Regression fixture: compile profile_zoom_reset.cs.fixture as
src/Bootstrap/ProfileZoomFixture.cs in an external source stage, attach it to
a temporary Node scene and run that scene headlessly with GUO_ZOOM_RESET_DIR
pointing at a new evidence data folder. It exercises the actual generated
Profile JSON metadata, ConfigurationResolver file load, ProfileManager file
save, camera initialization and live wide-zoom preservation. Inputs cover
0.5, 0.7, 1.25, 2.5, 2.6, 5 and 20, plus missing scale and inherited defaults.
Remove the injected scene/source before the required full smoke and export.
No account, shard, game data or timing benchmark is needed for this fixture.

Incident evidence: the killed owner's profile contained default_scale=20,
full-size mode and a 3840x2054 game view. The client was reopened at 2.5 while
the owner tested. No crash/hang dump or Windows application event was found;
high visible-world workload is a hypothesis, not a proven hang stack.
This fix prevents unsafe automatic restoration; it does not claim to cure
every fullscreen or manually selected high-zoom stall.
