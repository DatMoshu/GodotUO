// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;

namespace GUO.Store;

internal sealed partial class StoreDeploymentWindow : Window
{
    private StoreClient _store;
    private StoreContentLock _candidate;
    private Label _status;
    private bool _busy;
    private bool _managed;
    private bool _customStore;
    private GUO.Assets.UOFileManager _files;
    private readonly Dictionary<string, LineEdit> _ids = new();
    private string Active => Path.Combine(_store.Root, ".active-content.json");

    public static StoreDeploymentWindow Open(Node owner, string id, string version, StoreClient store = null, GUO.Assets.UOFileManager files = null)
    {
        var window = new StoreDeploymentWindow();
        window._customStore = store != null;
        window._store = store ?? StoreOptions.CreateClient(StoreAddress.Default);
        window._files = files ?? Client.Game.UO.FileManager;
        try
        {
            var closure = window._store.VerifyContent(id, version);
            window._candidate = new StoreContentLock { Pack = id, Version = version, IdentityHash = closure.IdentityHash };
            owner.AddChild(window);
            window.Build(closure);
            var viewport = owner.GetViewport().GetVisibleRect().Size;
            window.PopupCentered(new Vector2I(Math.Max(360, Math.Min(720, (int)viewport.X - 32)), Math.Max(320, Math.Min(640, (int)viewport.Y - 80))));
            return window;
        }
        catch { window._store.Dispose(); window.QueueFree(); throw; }
    }

    private void Build(StoreVerifiedContent closure)
    {
        Title = "Content deployment";
        MinSize = new Vector2I(360, 320);
        CloseRequested += QueueFree;
        var column = new VBoxContainer(); AddChild(column);
        column.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        column.AddChild(new Label { Text = "Assign the IDs required by your shard. Selection takes effect after restarting GUO.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        column.AddChild(new Label { Text = "Server content must be exported and deployed separately. Scripts keep their own approval controls.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; column.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(rows);
        StoreContentLock current = null;
        try { if (File.Exists(Active)) current = StoreContentLock.Read(Active); } catch { }
        foreach (var pack in closure.Packs.Values.OrderBy(p => p.Id))
            foreach (var component in pack.Manifest.Components ?? new())
            {
                string identity = pack.Id + ":" + component.Id;
                if (component.Type is "translation" or "wearable" or "script" or "item" or "region" or "decoration" or "loot" or "creature") continue;
                var row = new HBoxContainer(); rows.AddChild(row);
                row.AddChild(new Label { Text = identity + " (" + component.Type + ")", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart });
                var input = new LineEdit { PlaceholderText = "ID", CustomMinimumSize = new Vector2(100, 44) }; row.AddChild(input);
                if (current?.Bindings.TryGetValue(identity, out var existing) == true && existing.Type == component.Type) input.Text = existing.Id.ToString();
                _candidate.Bindings.Add(identity, new StoreContentBinding { Type = component.Type });
                _ids.Add(identity, input);
            }
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "No IDs are assigned automatically." }; column.AddChild(_status);
        var actions = new GridContainer { Columns = 2 }; column.AddChild(actions);
        AddButton(actions, "Import lock…", Import);
        AddButton(actions, "Validate & select", () => _ = Select());
        AddButton(actions, "Restore previous", () => _ = Select(true));
        AddButton(actions, "Export server…", Export);
        AddButton(actions, "Use original assets", () =>
        {
            if (_busy || _managed) return;
            try { StoreDeployment.Deactivate(Active); _status.Text = "Original assets selected for restart. The previous deployment is retained for rollback."; }
            catch (Exception e) { _status.Text = e.Message; }
        });
        AddButton(actions, "Close", QueueFree);
        if (!_customStore && (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("UO_CONTENT_LOCK"))
            || !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("UO_CONTENT_STORE"))))
        {
            _managed = true;
            foreach (var button in actions.GetChildren().OfType<Button>()) button.Disabled = button.Text != "Close";
            _status.Text = "Content is configured through environment settings. Manage that deployment with the command-line tools.";
        }
    }

    private void AddButton(Container owner, string text, Action pressed)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 44), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        owner.AddChild(button); button.Pressed += pressed;
    }

    private StoreContentLock ReadCandidate()
    {
        foreach (var (identity, input) in _ids)
        {
            StorePack.Require(int.TryParse(input.Text, out int id), "Enter a numeric ID for " + identity);
            _candidate.Bindings[identity].Id = id;
        }
        _candidate.Verify(_store);
        return _candidate;
    }

    internal async Task Select(bool rollback = false)
    {
        if (_busy || _managed) return;
        _busy = true;
        try
        {
            var candidate = rollback ? StoreContentLock.Read(Active + ".previous") : ReadCandidate();
            _status.Text = "Validating installed files, bindings and decoded client content…";
            // The dry run reads loaded archive metadata but never changes live
            // arrays, maps, textures or script state. Images belong to this worker.
            var files = _files;
            string language = Settings.GlobalSettings.Language;
            await Task.Run(() => { using var validated = StoreRuntimeContent.Load(files, language, candidate, _store, apply: false); });
            if (!IsInsideTree()) return;
            StoreDeployment.Activate(_store, candidate, Active);
            _status.Text = $"Selected {candidate.Pack} {candidate.Version}. Restart GUO to use it. Running assets have not changed.";
        }
        catch (Exception e) { if (IsInsideTree()) _status.Text = "Deployment unchanged: " + e.Message; }
        finally { _busy = false; if (!IsInsideTree()) _store.Dispose(); }
    }

    private void Export()
    {
        if (_busy || _managed) return;
        try
        {
            var candidate = ReadCandidate();
            var picker = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem,
                Title = "Export server definitions to a new file", CurrentFile = "server-content.json" };
            AddChild(picker);
            picker.FileSelected += path =>
            {
                string temporary = Path.Combine(_store.Root, ".export-lock-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(candidate));
                    StoreServerExport.Export(_store, temporary, path);
                    _status.Text = "Server definitions exported. Deploy them with the matching client IDs.";
                }
                catch (Exception e) { _status.Text = "Export failed: " + e.Message; }
                finally { if (File.Exists(temporary)) File.Delete(temporary); picker.QueueFree(); }
            };
            picker.Canceled += picker.QueueFree;
            picker.PopupCentered(new Vector2I(600, 440));
        }
        catch (Exception e) { _status.Text = e.Message; }
    }

    internal void Import(string path)
    {
        var imported = StoreContentLock.Read(path);
        imported.Verify(_store);
        StorePack.Require(imported.Pack == _candidate.Pack && imported.Version == _candidate.Version
            && imported.IdentityHash == _candidate.IdentityHash, "Lock belongs to a different pack or dependency closure");
        foreach (var (identity, input) in _ids)
            input.Text = imported.Bindings.TryGetValue(identity, out var binding) ? binding.Id.ToString() : "";
        _status.Text = "Bindings imported. Validate and select to use this deployment after restart.";
    }

    private void Import()
    {
        if (_busy || _managed) return;
        var picker = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, Title = "Import deployment lock" };
        AddChild(picker);
        picker.FileSelected += path =>
        {
            try { Import(path); } catch (Exception e) { _status.Text = "Import failed: " + e.Message; }
            finally { picker.QueueFree(); }
        };
        picker.Canceled += picker.QueueFree;
        picker.PopupCentered(new Vector2I(600, 440));
    }

    public override void _ExitTree() { if (!_busy) _store?.Dispose(); }
}
