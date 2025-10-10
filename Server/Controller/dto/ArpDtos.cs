// Server/Controller/dto/ArpDtos.cs
using System.ComponentModel.DataAnnotations;

namespace Server.Controller.dto
{
    public sealed class ArpRequestDto
    {
        [Required] public string NicId { get; set; } = default!;
        [Required] public string TargetIp { get; set; } = default!;
        public string? SrcIp { get; set; }
    }
}
