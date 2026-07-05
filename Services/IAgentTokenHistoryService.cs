using Toolbox.Models;

namespace Toolbox.Services;

public interface IAgentTokenHistoryService
{
    IReadOnlyList<AgentDailyTokenHistoryRecord> LoadHistory(DateTime today);
    IReadOnlyList<AgentDailyTokenHistoryRecord> SaveFromRecords(IEnumerable<AgentTokenUsageRecord> records, DateTime today);
}
