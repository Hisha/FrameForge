using FrameForge.Core.Templates;

namespace FrameForge.Core.Semantics.V2;

/// <summary>Ordered schema-v2 selection. The final identity is always the primary selection.</summary>
public sealed record SemanticSelection
{
    public static SemanticSelection Empty { get; } = new();

    public IReadOnlyList<SemanticId> OrderedIds { get; init; } = [];
    public SemanticId? PrimaryId => OrderedIds.Count == 0 ? null : OrderedIds[^1];
    public int Count => OrderedIds.Count;

    public SemanticSelection Replace(SemanticId? id) => id is null
        ? Empty
        : new SemanticSelection { OrderedIds = [id.Value] };

    public SemanticSelection Toggle(SemanticId id)
    {
        var next = OrderedIds.Where(item => item != id).ToList();
        if (next.Count == OrderedIds.Count) next.Add(id);
        return new SemanticSelection { OrderedIds = next };
    }

    public SemanticSelection SelectPrimary(SemanticId id)
    {
        var next = OrderedIds.Where(item => item != id).ToList();
        next.Add(id);
        return new SemanticSelection { OrderedIds = next };
    }

    public SemanticSelection Prune(UiDocument document)
    {
        var valid = document.Nodes.Select(node => node.Id)
            .Concat(document.CompositionRoots.Select(root => root.Id)).ToHashSet();
        var next = OrderedIds.Where(valid.Contains).Distinct().ToArray();
        return next.Length == 0 ? Empty : new SemanticSelection { OrderedIds = next };
    }
}

public sealed record SemanticSessionChange
{
    public required UiDocument Document { get; init; }
    public required SemanticSelection Selection { get; init; }
    public required IReadOnlyList<UiDiagnostic> Diagnostics { get; init; }
    public required string Description { get; init; }
    public bool Changed { get; init; }
    public bool Success { get; init; }
    public bool IsHistoryRestore { get; init; }
}

/// <summary>
/// The authoritative schema-v2 editing gateway. It owns document state, semantic selection,
/// bounded snapshot history and logical transaction boundaries; transient layouts and assets are
/// never stored in history.
/// </summary>
public sealed class SemanticEditingSession
{
    public const int DefaultHistoryLimit = 100;

    private sealed record Snapshot(UiDocument Document, SemanticSelection Selection, string Description);
    private sealed record Transaction(Snapshot Before, string Description, bool Changed);

    private readonly int _historyLimit;
    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    private UiDocumentEditor _editor;
    private BlizzardTemplateRegistry? _templates;
    private Transaction? _transaction;

    public SemanticEditingSession(UiDocument document, BlizzardTemplateRegistry? templates = null,
        SemanticSelection? selection = null, int historyLimit = DefaultHistoryLimit)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (historyLimit <= 0) throw new ArgumentOutOfRangeException(nameof(historyLimit));
        Document = document;
        Selection = (selection ?? SemanticSelection.Empty).Prune(document);
        _historyLimit = historyLimit;
        _templates = templates;
        _editor = new UiDocumentEditor(templates);
    }

    public UiDocument Document { get; private set; }
    public SemanticSelection Selection { get; private set; }
    public bool CanUndo => _undo.Count > 0 && _transaction is null;
    public bool CanRedo => _redo.Count > 0 && _transaction is null;
    public bool InTransaction => _transaction is not null;
    public string? UndoDescription => CanUndo ? _undo[^1].Description : null;
    public string? RedoDescription => CanRedo ? _redo[^1].Description : null;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    public void SetTemplateRegistry(BlizzardTemplateRegistry? templates)
    {
        _templates = templates;
        _editor = new UiDocumentEditor(templates);
    }

    public void ReplaceSelection(SemanticId? id) => Selection = Selection.Replace(id).Prune(Document);
    public void ToggleSelection(SemanticId id) => Selection = Selection.Toggle(id).Prune(Document);
    public void SelectPrimary(SemanticId id) => Selection = Selection.SelectPrimary(id).Prune(Document);

    public SemanticSessionChange Execute(string description,
        Func<UiDocumentEditor, UiDocument, SemanticEditResult> command,
        Func<SemanticSelection, SemanticEditResult, SemanticSelection>? selection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(command);
        // A transaction already owns its single before-snapshot. Continuous gestures therefore
        // do not allocate redundant history snapshots for every pointer event.
        var before = _transaction is null ? new Snapshot(Document, Selection, description) : null;
        var result = command(_editor, Document);
        if (!result.Success)
            return Change(result.Diagnostics, description, changed: false, success: false);

        var diagnostics = UiDocumentValidator.Validate(result.Document, _templates);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Change(diagnostics, description, changed: false, success: false);

        Document = result.Document;
        Selection = (selection?.Invoke(Selection, result) ?? Selection).Prune(Document);
        if (_transaction is { } transaction)
            _transaction = transaction with { Changed = true };
        else
        {
            Push(_undo, before!);
            _redo.Clear();
        }
        return Change(result.Diagnostics, description, changed: true, success: true);
    }

    public void BeginTransaction(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (_transaction is not null) throw new InvalidOperationException("A semantic edit transaction is already active.");
        _transaction = new Transaction(new Snapshot(Document, Selection, description), description, false);
    }

    public SemanticSessionChange CommitTransaction()
    {
        if (_transaction is not { } transaction)
            return Change([], "No transaction", changed: false, success: false);
        _transaction = null;
        if (!transaction.Changed)
            return Change([], transaction.Description, changed: false, success: true);
        Push(_undo, transaction.Before);
        _redo.Clear();
        return Change([], transaction.Description, changed: true, success: true);
    }

    public SemanticSessionChange CancelTransaction()
    {
        if (_transaction is not { } transaction)
            return Change([], "No transaction", changed: false, success: false);
        _transaction = null;
        var changed = transaction.Changed;
        Document = transaction.Before.Document;
        Selection = transaction.Before.Selection.Prune(Document);
        return Change([], transaction.Description, changed, success: true, history: true);
    }

    public SemanticSessionChange Undo()
    {
        if (!CanUndo) return Change([], "Nothing to undo", changed: false, success: false);
        var target = Pop(_undo);
        Push(_redo, new Snapshot(Document, Selection, target.Description));
        Document = target.Document;
        Selection = target.Selection.Prune(Document);
        return Change([], target.Description, changed: true, success: true, history: true);
    }

    public SemanticSessionChange Redo()
    {
        if (!CanRedo) return Change([], "Nothing to redo", changed: false, success: false);
        var target = Pop(_redo);
        Push(_undo, new Snapshot(Document, Selection, target.Description));
        Document = target.Document;
        Selection = target.Selection.Prune(Document);
        return Change([], target.Description, changed: true, success: true, history: true);
    }

    private SemanticSessionChange Change(IReadOnlyList<UiDiagnostic> diagnostics, string description,
        bool changed, bool success, bool history = false) => new()
    {
        Document = Document, Selection = Selection, Diagnostics = diagnostics,
        Description = description, Changed = changed, Success = success, IsHistoryRestore = history,
    };

    private void Push(List<Snapshot> history, Snapshot snapshot)
    {
        history.Add(snapshot);
        if (history.Count > _historyLimit) history.RemoveAt(0);
    }

    private static Snapshot Pop(List<Snapshot> history)
    {
        var result = history[^1];
        history.RemoveAt(history.Count - 1);
        return result;
    }
}
