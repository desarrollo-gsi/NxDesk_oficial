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
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace NxDesk.Client.Controls.WindowActions
{
    /// <summary>
    /// Lógica de interacción para WindowActionsControl.xaml
    /// </summary>
    public partial class WindowActionsControl : UserControl
    {
        private bool isHiding = false;

        public WindowActionsControl()
        {
            InitializeComponent();
        }

        private void MainButton_Click(object sender, RoutedEventArgs e)
        {
            ShowActions();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            var parentWindow = Window.GetWindow(this);
            if (parentWindow != null)
            {
                parentWindow.WindowState = WindowState.Minimized;
            }
            HideActions(); // Cierra el menú al hacer clic
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            var parentWindow = Window.GetWindow(this);
            if (parentWindow != null)
            {
                parentWindow.WindowState = parentWindow.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            HideActions(); // Cierra el menú al hacer clic
        }

        private void Logout_Click(object sender, RoutedEventArgs e)
        {

        }

        private void ActionsPanel_MouseLeave(object sender, MouseEventArgs e)
        {
            // Cierra el menú cuando el ratón sale del área del panel
            HideActions();
        }

        private void ShowActions()
        {
            isHiding = false;
            MainButton.Visibility = Visibility.Collapsed;
            ActionsPanel.Visibility = Visibility.Visible;

            var showAnimation = (Storyboard)FindResource("ShowActionsAnimation");
            showAnimation.Begin();
        }

        private void HideActions()
        {
            // Evita que se dispare múltiples veces la animación de ocultar
            if (isHiding || ActionsPanel.Visibility == Visibility.Collapsed) return;

            isHiding = true;
            var hideAnimation = (Storyboard)FindResource("HideActionsAnimation");

            // Cuando la animación termine, cambia la visibilidad
            hideAnimation.Completed += (s, e) =>
            {
                // Solo cambiar visibilidad si no se ha vuelto a pedir que se muestre
                if (isHiding)
                {
                    ActionsPanel.Visibility = Visibility.Collapsed;
                    MainButton.Visibility = Visibility.Visible;
                }
                isHiding = false;
            };

            hideAnimation.Begin();
        }
    }
}
