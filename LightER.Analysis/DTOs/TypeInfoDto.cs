namespace LightER.Analysis.Dtos
{
    public sealed class TypeInfoDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Namespace { get; set; } = "";
        public string Kind { get; set; } = "";
        public string File { get; set; } = "";
        public List<string> Inherits { get; set; } = new();
        public List<string> Implements { get; set; } = new();
        public List<UseRefDto> Uses { get; set; } = new();
        public List<string> UsedBy { get; set; } = new();
    }
}

