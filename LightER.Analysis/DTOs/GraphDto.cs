
namespace LightER.Analysis.Dtos
{
    public sealed class GraphDto
    {
        public List<TypeInfoDto> Types { get; set; } = new();
        public List<EdgeDto> Edges { get; set; } = new();
    }
}
