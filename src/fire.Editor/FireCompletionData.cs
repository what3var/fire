using System;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace fire.Editor
{
    /// <summary>Adapter, der ein bestehendes CompletionItem (aus
    /// CompletionEngine.GetSuggestions - unverändert wiederverwendet, siehe
    /// dort) als AvalonEdit-`ICompletionData` verfügbar macht. Reine
    /// Verpackung, keine eigene Logik - CompletionEngine bleibt komplett
    /// UI-unabhängig, wie zuvor.</summary>
    internal sealed class FireCompletionData : ICompletionData
    {
        public FireCompletionData(CompletionItem item) => Item = item;

        public CompletionItem Item { get; }

        public ImageSource? Image => null;
        public string Text => Item.Text;

        /// <summary>Was in der Liste angezeigt wird - `Display` (siehe
        /// CompletionItem) enthält bereits Name + ggf. Detail in Klammern
        /// (z.B. Parameteranzahl bei Methoden).</summary>
        public object Content => Item.Display;

        public object Description => Item.Detail ?? Item.Kind.ToString();

        public double Priority => Item.Score;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Item.Text);
    }
}
