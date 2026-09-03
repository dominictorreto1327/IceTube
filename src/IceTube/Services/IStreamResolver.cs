using System.Threading;
using System.Threading.Tasks;
using IceTube.Models;

namespace IceTube.Services
{
    public interface IStreamResolver
    {
        Task<VideoInfo> ResolveAsync(string url, CancellationToken cancellationToken);
    }

    public sealed class StreamResolutionException : System.Exception
    {
        public StreamResolutionException(string message) : base(message) { }
        public StreamResolutionException(string message, System.Exception innerException) : base(message, innerException) { }
    }
}
