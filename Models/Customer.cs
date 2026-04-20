namespace RiesgoFiscalApp.Models
{
    public class Customer
    {
        public string CuitCuil { get; set; } = string.Empty;
        public decimal MontoOperado { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public string Apellido { get; set; } = string.Empty;
        public int Edad { get; set; }
        public string Dni { get; set; } = string.Empty;
        
        // Clasificacion de cliente: PF - Monotributista, PF - Responsable Inscripto, PJ
        public string Clasificacion { get; set; } = string.Empty;
        
        // Datos Opcionales (pueden venir del Agente 1)
        public bool TieneRelacionDependencia { get; set; }
        public int MesesAntiguedadLaboral { get; set; }
        public bool MonotributoAlDia { get; set; }
        public string EstadoDeuda { get; set; } = string.Empty;
        public decimal LimiteOperativoEstimado { get; set; }
        public string CuitExtraidoDeDocumento { get; set; } = string.Empty;
    }
}