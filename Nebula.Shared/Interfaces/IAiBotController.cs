using Nebula.Shared.Models;

namespace Nebula.Shared.Interfaces
{
    public interface IAiBotController
    {
        string BotId { get; }
        void Update(GameState state);
    }
}
