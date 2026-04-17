using System.Threading.Tasks;

namespace Babel.Player.Services;

public interface IDialogService
{
    Task<bool> ShowWarmupNoticeAsync();
}
