using Hangfire;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class HangfireSieImportQueue(IBackgroundJobClient jobClient) : ISieImportQueue
{
    public void Enqueue(int runId, string fileName, uint stagingOid, bool overwrite, int? rarId) =>
        jobClient.Enqueue<SieImportJob>(job => job.RunAsync(runId, fileName, stagingOid, overwrite, rarId, null));
}
