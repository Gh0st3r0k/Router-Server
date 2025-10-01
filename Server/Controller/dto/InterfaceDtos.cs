#nullable enable
namespace WANT.Controller.Dto
{
    public sealed record SetAddressRequest(string Ip, string Mask);
    public sealed record SetDisplayNameRequest(string Name);
}