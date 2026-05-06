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
        private Customer? _currentCustomer;
        private RiskAssessment? _assessmentResult;
        private bool _isBusy;
        private string _statusMessage = string.Empty;
        private string _selectedModel;
        private bool _isOnboarded = false;
        private bool _canUnlockFeatures = false;

        public MainViewModel()
        {
            _agentService = new OpenAIAgentService();
            
            Documents = new ObservableCollection<Document>();
            CustomerTypes = new List<string> { 
                "PF - Monotributista", 
                "PF - Responsable Inscripto", 
                "PF - Relación Dependencia",
                "Persona Jurídica (S.A./S.R.L.)" 
            };
            AvailableModels = new List<string> { "Gemini 3 Flash Preview", "OpenAI GPT-4o", "Claude 3.5 Sonnet" };
            _selectedModel = "Gemini 3 Flash Preview";
            
            EvaluateCommand = new RelayCommand(async _ => await EvaluateRiskAsync(), _ => CanAnalyze());
            AddDocumentCommand = new RelayCommand(async param => await PickAndAddDocumentAsync(param as string));
            FinishOnboardingCommand = new RelayCommand(_ => IsOnboarded = true, _ => CanUnlockFeatures);

            CurrentCustomer = new Customer { Clasificacion = "PF - Monotributista" };
            StatusMessage = "Seleccione su perfil fiscal para ver los requisitos.";
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
            get => _currentCustomer!;
            set 
            { 
                if (_currentCustomer != null) _currentCustomer.PropertyChanged -= OnCustomerPropertyChanged;
                _currentCustomer = value; 
                if (_currentCustomer != null) _currentCustomer.PropertyChanged += OnCustomerPropertyChanged;
                OnPropertyChanged(); 
                ValidateInputs(); 
            }
        }

        public bool IsMonotributista => CurrentCustomer?.Clasificacion == "PF - Monotributista";
        public bool IsResponsableInscripto => CurrentCustomer?.Clasificacion == "PF - Responsable Inscripto";
        public bool IsRelacionDependencia => CurrentCustomer?.Clasificacion == "PF - Relación Dependencia";
        public bool IsPersonaJuridica => CurrentCustomer?.Clasificacion == "Persona Jurídica (S.A./S.R.L.)";

        private void OnCustomerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Customer.Clasificacion))
            {
                Documents.Clear(); // Reiniciamos docs al cambiar de perfil
                OnPropertyChanged(nameof(IsMonotributista));
                OnPropertyChanged(nameof(IsResponsableInscripto));
                OnPropertyChanged(nameof(IsRelacionDependencia));
                OnPropertyChanged(nameof(IsPersonaJuridica));
            }
            ValidateInputs();
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

        private bool CanAnalyze()
        {
            if (Documents == null || _currentCustomer == null) return false;
            
            bool hasDdjj = Documents.Any(d => d.Tipo == "DDJJ");
            if (!hasDdjj || !CurrentCustomer.AceptaDeclaracionJurada) return false;

            switch (CurrentCustomer.Clasificacion)
            {
                case "PF - Monotributista":
                    return Documents.Any(d => d.Tipo == "Comprobante Monotributo") && 
                           Documents.Count(d => d.Tipo == "Factura") >= 3;

                case "PF - Responsable Inscripto":
                    return Documents.Count(d => d.Tipo == "Posición IVA") >= 3 && 
                           Documents.Any(d => d.Tipo == "Autónomo") &&
                           Documents.Any(d => d.Tipo == "Factura");

                case "PF - Relación Dependencia":
                    return Documents.Any(d => d.Tipo == "Recibo Sueldo");

                case "Persona Jurídica (S.A./S.R.L.)":
                    return Documents.Any(d => d.Tipo == "Estatuto") && 
                           Documents.Count(d => d.Tipo == "Posición IVA") >= 3;

                default:
                    return false;
            }
        }

        private void ValidateInputs()
        {
            if (EvaluateCommand == null) return;
            EvaluateCommand.RaiseCanExecuteChanged();

            if (!IsBusy && _currentCustomer != null)
            {
                var missing = new List<string>();
                if (!Documents.Any(d => d.Tipo == "DDJJ")) missing.Add("DDJJ");

                switch (CurrentCustomer.Clasificacion)
                {
                    case "PF - Monotributista":
                        if (!Documents.Any(d => d.Tipo == "Comprobante Monotributo")) missing.Add("Comprobante Monotributo");
                        int facturas = Documents.Count(d => d.Tipo == "Factura");
                        if (facturas < 3) missing.Add($"Facturas ({facturas}/3)");
                        break;

                    case "PF - Responsable Inscripto":
                        int ivas = Documents.Count(d => d.Tipo == "Posición IVA");
                        if (ivas < 3) missing.Add($"Posiciones IVA ({ivas}/3)");
                        if (!Documents.Any(d => d.Tipo == "Autónomo")) missing.Add("Credencial Autónomo");
                        if (!Documents.Any(d => d.Tipo == "Factura")) missing.Add("Factura");
                        break;

                    case "PF - Relación Dependencia":
                        if (!Documents.Any(d => d.Tipo == "Recibo Sueldo")) missing.Add("Recibo Sueldo");
                        break;
                }

                if (missing.Any()) StatusMessage = "Faltante: " + string.Join(", ", missing);
                else StatusMessage = "Documentación completa. Inicie auditoría.";
            }
        }

        private async Task PickAndAddDocumentAsync(string? docType)
        {
            if (string.IsNullOrEmpty(docType)) return;

            var topLevel = TopLevel.GetTopLevel((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Adjuntar {docType} (PDF)",
                AllowMultiple = true,
                FileTypeFilter = new[] { FilePickerFileTypes.Pdf }
            });

            foreach (var file in files)
            {
                if (!file.Name.ToLower().EndsWith(".pdf")) continue;

                Documents.Add(new Document 
                { 
                    Tipo = docType, 
                    FileName = file.Name,
                    FilePath = file.Path.LocalPath,
                    ContenidoTexto = "Listo para auditoría..." 
                });
            }
            StatusMessage = $"Documentos cargados exitosamente.";
            ValidateInputs();
        }

        private async Task EvaluateRiskAsync()
        {
            IsBusy = true;
            AssessmentResult = null;
            CanUnlockFeatures = false;

            try
            {
                StatusMessage = "Agentes de IA auditando perfiles fiscales...";
                var docList = new List<Document>(Documents);
                CurrentCustomer = await _agentService.ExtractDataFromDocumentsAsync(docList, CurrentCustomer, SelectedModel);
                OnPropertyChanged(nameof(CurrentCustomer)); 
                
                AssessmentResult = await _agentService.EvaluateRiskAsync(CurrentCustomer, SelectedModel);
                StatusMessage = AssessmentResult.DictamenPreliminar == "Cumple" ? "Perfil Aprobado." : "Perfil Rechazado.";
                CanUnlockFeatures = AssessmentResult.DictamenPreliminar == "Cumple";
            }
            catch (Exception ex) { StatusMessage = $"❌ ERROR: {ex.Message}"; }
            finally { IsBusy = false; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}