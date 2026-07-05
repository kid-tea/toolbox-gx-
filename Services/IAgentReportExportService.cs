using Toolbox.Models;

namespace Toolbox.Services;

public interface IAgentReportExportService
{
    string BuildMarkdown(AgentScanReport report, AgentReportPrivacyOptions privacyOptions);
}
