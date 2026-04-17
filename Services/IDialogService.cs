using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public interface IDialogService
{
    Task<bool> ShowWarmupNoticeAsync(CancellationToken cancellationToken = default);
}
