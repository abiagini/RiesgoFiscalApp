using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RiesgoFiscalApp.Models
{
    public class Customer : INotifyPropertyChanged
    {
        private string _cuitCuil = string.Empty;
        private decimal _montoOperado;
        private string _nombre = string.Empty;
        private string _clasificacion = string.Empty;
        private bool _aceptaDeclaracionJurada;
        private bool _esPep;
        private int _edad = 18;
        private bool _isJuridica;

        public string CuitCuil 
        { 
            get => _cuitCuil; 
            set { _cuitCuil = value; OnPropertyChanged(); } 
        }

        public decimal MontoOperado 
        { 
            get => _montoOperado; 
            set { _montoOperado = value; OnPropertyChanged(); } 
        }

        public string Nombre 
        { 
            get => _nombre; 
            set { _nombre = value; OnPropertyChanged(); } 
        }

        public string Clasificacion 
        { 
            get => _clasificacion; 
            set { _clasificacion = value; OnPropertyChanged(); } 
        }

        public bool AceptaDeclaracionJurada 
        { 
            get => _aceptaDeclaracionJurada; 
            set { _aceptaDeclaracionJurada = value; OnPropertyChanged(); } 
        }

        public bool EsPep 
        { 
            get => _esPep; 
            set { _esPep = value; OnPropertyChanged(); } 
        }

        public int Edad 
        { 
            get => _edad; 
            set { _edad = value; OnPropertyChanged(); } 
        }

        public bool IsJuridica
        {
            get => _isJuridica;
            set { _isJuridica = value; OnPropertyChanged(); }
        }

        public string Apellido { get; set; } = string.Empty;
        public string Dni { get; set; } = string.Empty;
        
        // Datos extraídos por IA
        public string CuitExtraidoDeDocumento { get; set; } = string.Empty;
        public decimal SueldoNetoExtraido { get; set; }
        public bool EsPepSegunDocumento { get; set; }
        public string NombreExtraidoRecibo { get; set; } = string.Empty;
        public string NombreExtraidoDdjj { get; set; } = string.Empty;
        public int MesesAntiguedadLaboral { get; set; }
        public string DomicilioRecibo { get; set; } = string.Empty;
        public string DomicilioDdjj { get; set; } = string.Empty;
        public string MesRecibo { get; set; } = string.Empty;
        public string CuitEmpleador { get; set; } = string.Empty;

        // Nuevos campos para Persona Jurídica
        public string RazonSocial { get; set; } = string.Empty;
        public string PosicionIva { get; set; } = string.Empty;
        public decimal FacturacionAnual { get; set; }
        public string RepresentanteLegalNombre { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}