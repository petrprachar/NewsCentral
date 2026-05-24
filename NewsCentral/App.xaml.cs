using Microsoft.Maui;
using Microsoft.Maui.Controls;
using NewsCentral.Services;

namespace NewsCentral
{
    public partial class App : Application
    {
        private readonly DataSeederService _dataSeeder;

        public App(DataSeederService dataSeeder)
        {
            InitializeComponent();
            _dataSeeder = dataSeeder;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new MainPage()) { Title = "NewsCentral" };

            // Initialize data in background after window is created
            _ = InitializeDataAsync();

            return window;
        }

        private async Task InitializeDataAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== App: Starting background initialization ===");

                await _dataSeeder.InitializeIfNeededAsync();

                System.Diagnostics.Debug.WriteLine("=== App: Background initialization complete ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ERROR during background initialization: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }
    }
}
