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
            CustomerTypes = new List<string> { "PF - Monotributista", "PF - Responsable Inscripto", "Persona Jurídica (S.A./S.R.L.)" };
            AvailableModels = new List<string> { "Gemini 3 Flash Preview", "OpenAI GPT-4o", "Claude 3.5 Sonnet" };
            _selectedModel = "Gemini 3 Flash Preview";
            
            EvaluateCommand = new RelayCommand(async _ => await EvaluateRiskAsync(), _ => CanAnalyze());
            AddDocumentCommand = new RelayCommand(async param => await PickAndAddDocumentAsync(param as string));
            FinishOnboardingCommand = new RelayCommand(_ => IsOnboarded = true, _ => CanUnlockFeatures);

            CurrentCustomer = new Customer { Clasificacion = "PF - Monotributista", IsJuridica = false };
            StatusMessage = "Seleccione el tipo de persona para comenzar.";
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

        private void OnCustomerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Customer.Clasificacion))
            {
                CurrentCustomer.IsJuridica = CurrentCustomer.Clasificacion.Contains("Jurídica");
                Documents.Clear(); // Limpiamos docs al cambiar tipo de persona
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
            
            if (CurrentCustomer.IsJuridica)
            {
                bool hasEstatuto = Documents.Any(d => d.Tipo == "Estatuto");
                bool hasIva = Documents.Any(d => d.Tipo == "Posición IVA");
                bool hasBalance = Documents.Any(d => d.Tipo == "Balance");
                return !IsBusy && !string.IsNullOrWhiteSpace(CurrentCustomer.RazonSocial) && 
                       !string.IsNullOrWhiteSpace(CurrentCustomer.CuitCuil) &&
                       hasEstatuto && hasIva && hasBalance && CurrentCustomer.AceptaDeclaracionJurada;
            }
            else
            {
                bool hasRecibo = Documents.Any(d => d.Tipo == "Recibo Sueldo");
                bool hasDdjj = Documents.Any(d => d.Tipo == "DDJJ");
                return !IsBusy && !string.IsNullOrWhiteSpace(CurrentCustomer.Nombre) && 
                       !string.IsNullOrWhiteSpace(CurrentCustomer.CuitCuil) &&
                       hasRecibo && hasDdjj && CurrentCustomer.AceptaDeclaracionJurada;
            }
        }

        private void ValidateInputs()
        {
            if (EvaluateCommand == null) return;
            EvaluateCommand.RaiseCanExecuteChanged();

            if (!IsBusy && _currentCustomer != null)
            {
                if (CanAnalyze())
                {
                    StatusMessage = "Requisitos completos. Inicie el análisis de IA.";
                }
                else
                {
                    var missing = new List<string>();
                    if (CurrentCustomer.IsJuridica)
                    {
                        if (string.IsNullOrWhiteSpace(CurrentCustomer.RazonSocial)) missing.Add("Razón Social");
                        if (string.IsNullOrWhiteSpace(CurrentCustomer.CuitCuil)) missing.Add("CUIT");
                        if (!Documents.Any(d => d.Tipo == "Estatuto")) missing.Add("Estatuto");
                        if (!Documents.Any(d => d.Tipo == "Posición IVA")) missing.Add("Posición IVA");
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(CurrentCustomer.Nombre)) missing.Add("Nombre");
                        if (string.IsNullOrWhiteSpace(CurrentCustomer.CuitCuil)) missing.Add("CUIT");
                        if (!Documents.Any(d => d.Tipo == "Recibo Sueldo")) missing.Add("Recibo");
                    }
                    if (!CurrentCustomer.AceptaDeclaracionJurada) missing.Add("Aceptar DDJJ");
                    
                    StatusMessage = "Faltante: " + string.Join(", ", missing);
                }
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
                if (!file.Name.ToLower().EndsWith(".pdf"))
                {
                    StatusMessage = "Error: Solo se permiten archivos PDF.";
                    return;
                }

                // Si ya existe uno del mismo tipo, lo reemplazamos
                var existing = Documents.FirstOrDefault(d => d.Tipo == docType);
                if (existing != null) Documents.Remove(existing);

                Documents.Add(new Document 
                { 
                    Tipo = docType, 
                    FileName = file.Name,
                    FilePath = file.Path.LocalPath,
                    ContenidoTexto = $"Archivo '{file.Name}' listo para análisis..." 
                });
                StatusMessage = $"Documento '{docType}' cargado.";
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
                StatusMessage = "Auditando documentación con Agentes de IA...";
                var docList = new List<Document>(Documents);
                CurrentCustomer = await _agentService.ExtractDataFromDocumentsAsync(docList, CurrentCustomer, SelectedModel);
                OnPropertyChanged(nameof(CurrentCustomer)); 
                
                StatusMessage = "Generando dictamen de riesgo...";
                AssessmentResult = await _agentService.EvaluateRiskAsync(CurrentCustomer, SelectedModel);
                
                if (AssessmentResult.DictamenPreliminar == "Cumple")
                {
                    StatusMessage = "Validación Exitosa. Perfil aprobado.";
                    CanUnlockFeatures = true;
                }
                else
                {
                    StatusMessage = "Rechazado por inconsistencias fiscales/legales.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"❌ ERROR: {ex.Message}";
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