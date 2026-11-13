using SharedKernel.Cqrs;
using SharedKernel.Telemetry;

namespace SharedKernel.PipelineBehaviors;

public sealed class PublishTelemetryEventsPipelineBehavior<TRequest, TResponse>(
    ITelemetryEventsCollector telemetryEventsCollector,
    TelemetryClient telemetryClient,
    ConcurrentCommandCounter concurrentCommandCounter
) : IPipelineBehavior<TRequest, TResponse> where TRequest : ICommand where TResponse : ResultBase
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next(cancellationToken);

        if (concurrentCommandCounter.IsZero())
        {
            while (telemetryEventsCollector.HasEvents)
            {
                var telemetryEvent = telemetryEventsCollector.Dequeue();
                telemetryClient.TrackEvent(telemetryEvent.GetType().Name, telemetryEvent.Properties);
            }
        }

        return response;
    }
}
