using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Application.Services;

public interface IBackgroundJobRunService
{
    Task<BackgroundJobRun> CreateRunAsync(BackgroundJobType jobType, int? totalCount = null);
    Task<List<BackgroundJobRun>> GetOpenRunsAsync(BackgroundJobType jobType);
    Task AcknowledgeAsync(int runId);
}
