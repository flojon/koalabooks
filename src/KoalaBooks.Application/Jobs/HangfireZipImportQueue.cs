using Hangfire;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class HangfireZipImportQueue(IBackgroundJobClient jobClient) : IZipImportQueue
{
    public void Enqueue(int batchId) =>
        jobClient.Enqueue<ZipImportJob>(job => job.RunAsync(batchId));
}
