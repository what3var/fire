using System;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace fire.Editor
{
    /// <summary>Adapter that makes an existing CompletionItem (from CompletionEngine.GetSuggestions - reused unchanged, see there) available as an AvaloniaEdit `ICompletionData`.
    /// Pure packaging, no logic of its own - CompletionEngine remains completely UI-independent.</summary>
    internal sealed class FireCompletionData : ICompletionData
    {
        public FireCompletionData(CompletionItem item) => Item = item;

        public CompletionItem Item { get; }

        public IImage? Image => null;
        public string Text => Item.Text;

        /// <summary>What is shown in the list - `Display` (see CompletionItem) already contains name + possibly detail in parentheses (e.g. parameter count for methods).</summary>
        public object Content => Item.Display;

        /// <summary>The tooltip next to the list: the symbol with its `///` documentation if it has one, otherwise just the detail text.</summary>
        public object Description => Item.Documentation is { } doc
            ? DocToolTip.Build(Item.Display, doc)
            : Item.Detail ?? Item.Kind.ToString();

        public double Priority => Item.Score;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Item.Text);
    }
}
