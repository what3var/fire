using System;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace fire.Editor
{
    /// <summary>Adapter, der ein bestehendes CompletionItem (aus CompletionEngine.GetSuggestions - unverändert wiederverwendet, siehe dort) als AvaloniaEdit-`ICompletionData`
    /// verfügbar macht. Reine Verpackung, keine eigene Logik - CompletionEngine bleibt komplett UI-unabhängig.</summary>
    internal sealed class FireCompletionData : ICompletionData
    {
        public FireCompletionData(CompletionItem item) => Item = item;

        public CompletionItem Item { get; }

        public IImage? Image => null;
        public string Text => Item.Text;

        /// <summary>Was in der Liste angezeigt wird - `Display` (siehe CompletionItem) enthält bereits Name + ggf. Detail in Klammern (z.B. Parameteranzahl bei Methoden).</summary>
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
