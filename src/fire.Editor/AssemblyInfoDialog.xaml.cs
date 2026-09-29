using fire.Compiler.Assembly;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace fire.Editor
{
    /// <summary>
    /// Interaktionslogik für AssemblyInfo.xaml
    /// </summary>
    public partial class AssemblyInfoDialog : Window
    {
        public AssemblyInfoDialog()
        {
            InitializeComponent();
        }

        private void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = e.Text.Any(c => !char.IsDigit(c));
        }

        private void TextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(String)))
            {
                String text = (String)e.DataObject.GetData(typeof(String));
                if (text.Any(c => !char.IsDigit(c)))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Icons (*.ico)|*.ico|Alle Dateien (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;

            if (this.DataContext is AssemblyInfo info)
            {
                info.IconPath = dlg.FileName;
            }
        }
    }
}
