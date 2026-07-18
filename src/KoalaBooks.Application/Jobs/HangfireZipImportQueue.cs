using Hangfire;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class HangfireZipImportQueue(IBackgroundJobClient jobClient) : IZipImportQueue
{
    public void Enqueue(int runId, string fileName, uint stagingOid) =>
        jobClient.Enqueue<ZipImportJob>(job => job.RunAsync(runId, fileName, stagingOid, null));
}
