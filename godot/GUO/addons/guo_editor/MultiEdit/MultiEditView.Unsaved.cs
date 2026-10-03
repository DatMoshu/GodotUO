#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using Godot;

/// <summary>What the person chose in the unsaved-changes prompt.</summary>
public enum UnsavedChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// Unsaved changes (ADR-0031, phase 2): the document knows whether it differs from what was last saved or opened (by
/// content, so undoing back to the saved state clears the mark); New, Open and Import ask first; the title shows the
/// file name with a modified mark; and a close request with changes writes a recovery description beside the others
/// (an add-on cannot stop the editor from quitting).
/// </summary>
public partial class MultiEditView
{
    private int _savedHash;
    private string _filePath;
    private Label _title;
    private ConfirmationDialog _unsavedDialog;
    private Action _pending;
    private string _windowTitle0;

    /// <summary>A prompt is waiting for an answer.</summary>
    public bool UnsavedPromptOpen => _pending != null;

    /// <summary>The document differs from the last save or open.</summary>
    public bool Modified => _doc != null && ContentHash() != _savedHash;

    /// <summary>The file name with a modified mark, as the title shows it.</summary>
    public string TitleText
    {
        get
        {
            string file = _filePath != null ? Path.GetFileName(_filePath)
                : _doc.Source is int s ? $"client multi 0x{s:X4}" : MultiStore.SafeName(_doc.Name) + ".multi.json";
            return file + (Modified ? " *" : "");
        }
    }

    private int ContentHash()
    {
        var h = new HashCode();
        foreach (MultiPart p in _doc.Parts)
        {
            h.Add(p.Id);
            h.Add(p.X);
            h.Add(p.Y);
            h.Add(p.Z);
            h.Add(p.Shown);
            h.Add(p.Hue);
        }

        return h.ToHashCode();
    }

    /// <summary>Says the document as it is now is what is on disk (or was just opened).</summary>
    private void MarkSaved(string path = null, bool keepPath = false)
    {
        _savedHash = ContentHash();
        if (!keepPath)
        {
            _filePath = path;
        }

        UpdateTitle();
    }

    private void BuildTitle(Control bar)
    {
        _title = new Label { Text = "", CustomMinimumSize = new Vector2(190, 0), ClipText = true, TooltipText = "The file being edited; * means unsaved changes" };
        bar.AddChild(_title);
        VisibilityChanged += UpdateTitle;
    }

    private void UpdateTitle()
    {
        if (_title == null || _doc == null)
        {
            return;
        }

        _title.Text = TitleText;
        if (IsInsideTree() && IsVisibleInTree() && GetWindow() is Window w)
        {
            _windowTitle0 ??= w.Title;
            w.Title = $"{TitleText} - Multi Editor - {_windowTitle0}";
        }
        else if (_windowTitle0 != null && IsInsideTree() && GetWindow() is Window w2)
        {
            w2.Title = _windowTitle0;
        }
    }

    /// <summary>Runs <paramref name="proceed"/> now, or after the user decides what to do with unsaved changes.</summary>
    internal void GuardUnsaved(Action proceed)
    {
        if (!Modified)
        {
            proceed();
            return;
        }

        _pending = proceed;
        ShowUnsavedDialog();
    }

    private void ShowUnsavedDialog()
    {
        if (!IsInsideTree())
        {
            return;
        }

        if (_unsavedDialog == null)
        {
            _unsavedDialog = new ConfirmationDialog { Title = "Unsaved changes", OkButtonText = "Save", CancelButtonText = "Cancel" };
            _unsavedDialog.AddButton("Discard", true, "discard");
            _unsavedDialog.Confirmed += () => ResolveUnsaved(UnsavedChoice.Save);
            _unsavedDialog.Canceled += () => ResolveUnsaved(UnsavedChoice.Cancel);
            _unsavedDialog.CustomAction += a =>
            {
                _unsavedDialog.Hide();
                ResolveUnsaved(UnsavedChoice.Discard);
            };
            AddChild(_unsavedDialog);
        }

        _unsavedDialog.DialogText = $"{_doc.Name} has changes that are not saved.\nSave them as a description first?";
        _unsavedDialog.PopupCentered();
    }

    /// <summary>The answer to the prompt: Save writes the description then goes on, Discard goes on, Cancel stays.</summary>
    public void ResolveUnsaved(UnsavedChoice choice)
    {
        Action go = _pending;
        _pending = null;
        if (go == null)
        {
            return;
        }

        if (choice == UnsavedChoice.Cancel)
        {
            _status.Text = "kept your changes";
            return;
        }

        if (choice == UnsavedChoice.Save)
        {
            SaveDescription();
        }

        go();
    }

    /// <summary>Writes the document beside the other descriptions as NAME.recovery.multi.json (a close with changes does this).</summary>
    public string SaveRecovery()
    {
        string path = MultiStore.SaveDescription(MultiStore.SafeName(_doc.Name) + ".recovery", _doc.Source, _doc.Parts);
        _status.Text = $"unsaved changes were kept in {Path.GetFileName(path)}";
        return path;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest && _doc != null && _built && Modified)
        {
            SaveRecovery();
            ShowUnsavedDialog();
            _pending = () => { };
        }
    }
}
#endif
