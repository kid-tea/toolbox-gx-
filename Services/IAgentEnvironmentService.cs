using Toolbox.Models;

namespace Toolbox.Services;

public interface IAgentEnvironmentService
{
    Task<AgentScanReport> ScanAsync(AgentScanOptions options, CancellationToken cancellationToken = default);
    Task SaveSkillDescriptionAsync(AgentSkillItem skill, string language, CancellationToken cancellationToken = default);
}
