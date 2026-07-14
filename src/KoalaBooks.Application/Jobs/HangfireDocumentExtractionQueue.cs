using Hangfire;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class HangfireDocumentExtractionQueue(IBackgroundJobClient jobClient) : IDocumentExtractionQueue
{
    public void Enqueue(int documentId) =>
        jobClient.Enqueue<DocumentExtractionJob>(job => job.RunAsync(documentId));
}
