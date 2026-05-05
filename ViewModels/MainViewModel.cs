using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using RiesgoFiscalApp.Models;
using RiesgoFiscalApp.Services;

namespace RiesgoFiscalApp.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly IAgentService _agentService;
        private Customer _currentCustomer;
        private RiskAssessment? _assessmentResult;
        private bool _isBusy;
        private string _statusMessage = string.Empty;
        private string _selectedModel;
        private bool _isOnboarded = false;
        private bool _canUnlockFeatures = false;

        public MainViewModel()
        {
            _agentService = new OpenAIAgentService();
            _currentCustomer = new Customer { Clasificacion = "PF - Monotributista" };
            CustomerTypes = new List<string> { "PF - Monotributista", "PF - Responsable Inscripto", "PJ" };
            AvailableModels = new List<string> { "Gemini 3 Flash Preview", "OpenAI GPT-4o", "Claude 3.5 Sonnet" };
            _selectedModel = "Gemini 3 Flash Preview";
            Documents = new ObservableCollection<Document>();
            StatusMessage = "Complete todos los campos requeridos para habilitar el análisis.";
            
            EvaluateCommand = new RelayCommand(async _ => await EvaluateRiskAsync(), _ => CanAnalyze());
            AddDocumentCommand = new RelayCommand(async param => await PickAndAddDocumentAsync(param as string));
            FinishOnboardingCommand = new RelayCommand(_ => IsOnboarded = true, _ => CanUnlockFeatures);
        }

        public List<string> CustomerTypes { get; }
        public List<string> AvailableModels { get; }

        public string SelectedModel
        {
            get => _selectedModel;
            set { _selectedModel = value; OnPropertyChanged(); }
        }

        public bool IsOnboarded
        {
            get => _isOnboarded;
            set { _isOnboarded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowOnboarding)); }
        }

        public bool ShowOnboarding => !IsOnboarded;

        public bool CanUnlockFeatures
        {
            get => _canUnlockFeatures;
            set { _canUnlockFeatures = value; OnPropertyChanged(); ((RelayCommand)FinishOnboardingCommand).RaiseCanExecuteChanged(); }
        }

        public Customer CurrentCustomer
        {
            get => _currentCustomer;
            set { _currentCustomer = value; OnPropertyChanged(); ValidateInputs(); }
        }

        public RiskAssessment? AssessmentResult
        {
            get => _assessmentResult;
            set { _assessmentResult = value; OnPropertyChanged(); }
        }

        public ObservableCollection<Document> Documents { get; }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); ValidateInputs(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public RelayCommand EvaluateCommand { get; }
        public ICommand AddDocumentCommand { get; }
        public ICommand FinishOnboardingCommand { get; }

        // Lógica de validación de campos requeridos
        private bool CanAnalyze()
        {
            return !IsBusy &&
                   !string.IsNullOrWhiteSpace(CurrentCustomer.Nombre) &&
                   !string.IsNullOrWhiteSpace(CurrentCustomer.CuitCuil) &&
                   !string.IsNullOrWhiteSpace(CurrentCustomer.Clasificacion) &&
                   CurrentCustomer.MontoOperado > 0;
        }

        private void ValidateInputs()
        {
            EvaluateCommand.RaiseCanExecuteChanged();
            if (!CanAnalyze() && !IsBusy)
            {
                StatusMessage = "Campos requeridos pendientes: Nombre, CUIT, Clasificación y Monto > 0.";
            }
            else if (!IsBusy && !Documents.Any())
            {
                StatusMessage = "Campos completos. Se recomienda adjuntar PDFs para un mejor análisis.";
            }
        }

        private async Task PickAndAddDocumentAsync(string? docType)
        {
            if (string.IsNullOrEmpty(docType)) return;

            var topLevel = TopLevel.GetTopLevel((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Seleccionar {docType} (PDF)",
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.Pdf }
            });

            if (files.Count >= 1)
            {
                var file = files[0];
                
                // Validación adicional de extensión para máxima seguridad
                if (!file.Name.ToLower().EndsWith(".pdf"))
                {
                    StatusMessage = "Error: Solo se permiten archivos en formato PDF.";
                    return;
                }

                Documents.Add(new Document 
                { 
                    Tipo = docType, 
                    FileName = file.Name,
                    FilePath = file.Path.LocalPath,
                    ContenidoTexto = $"Archivo PDF '{file.Name}' listo para análisis de IA..." 
                });
                StatusMessage = $"Archivo '{file.Name}' adjuntado correctamente.";
                ValidateInputs();
            }
        }

        private async Task EvaluateRiskAsync()
        {
            IsBusy = true;
            AssessmentResult = null;
            CanUnlockFeatures = false;

            try
            {
                StatusMessage = $"Agente 1 (Lector PDF): Procesando perfil de {CurrentCustomer.Nombre}...";
                var docList = new List<Document>(Documents);
                CurrentCustomer = await _agentService.ExtractDataFromDocumentsAsync(docList, CurrentCustomer, SelectedModel);
                OnPropertyChanged(nameof(CurrentCustomer)); 
                
                StatusMessage = $"Agente 2 (Fiscal): Analizando coherencia con {SelectedModel}...";
                AssessmentResult = await _agentService.EvaluateRiskAsync(CurrentCustomer, SelectedModel);
                
                if (AssessmentResult.DictamenPreliminar == "Cumple")
                {
                    StatusMessage = "Validación Exitosa. Puede activar su cuenta.";
                    CanUnlockFeatures = true;
                }
                else
                {
                    StatusMessage = "Análisis finalizado con observaciones de riesgo.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ ERROR: {ex.Message}";
                AssessmentResult = null;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}