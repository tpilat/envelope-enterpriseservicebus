﻿//using Envelope.EnterpriseServiceBus.MessageHandlers;
//using Envelope.EnterpriseServiceBus.Orchestrations.Model;
//using Envelope.EnterpriseServiceBus.Orchestrations.Persistence;
//using Envelope.Services;
//using Envelope.Trace;

//namespace Envelope.EnterpriseServiceBus.Orchestrations.EventHandlers.Internal;

//internal class OrchestrationEventHandler : IAsyncEventHandler<OrchestrationEvent, OrchestrationEventHandlerContext>, IDisposable
//{
//	public Type? InterceptorType { get; set; } = typeof(AsyncEventHandlerInterceptor<OrchestrationEvent>);

//	private readonly IOrchestrationRepository _orchestrationRepository;

//	public OrchestrationEventHandler(IOrchestrationRepository orchestrationRepository)
//	{
//		_orchestrationRepository = orchestrationRepository ?? throw new ArgumentNullException(nameof(orchestrationRepository));
//	}

//	public async Task<IResult> HandleAsync(OrchestrationEvent @event, OrchestrationEventHandlerContext handlerContext, CancellationToken cancellationToken = default)
//	{
//		@event.Id = handlerContext.MessageId;

//		var result = new ResultBuilder();
//		var traceInfo = TraceInfo.Create(handlerContext.TraceInfo);

//		var saveResult = await _orchestrationRepository.EnqueueAsync(@event, traceInfo, cancellationToken).ConfigureAwait(false);
//		result.MergeErrors(saveResult);
//		return result.Build();
//	}

//	public void Dispose()
//	{
//	}
//}



using Envelope.EnterpriseServiceBus.MessageHandlers;
using Envelope.EnterpriseServiceBus.Orchestrations.Model;
using Envelope.EnterpriseServiceBus.Queues;
using Envelope.Services;
using Envelope.Trace;
using Microsoft.Extensions.DependencyInjection;

namespace Envelope.EnterpriseServiceBus.Orchestrations.EventHandlers;

public static class OrchestrationEventHandler
{
	public static async Task<MessageHandlerResult> HandleMessageAsync(IQueuedMessage<OrchestrationEvent> message, IMessageHandlerContext context, CancellationToken cancellationToken)
	{
		var result = new ResultBuilder();
		var traceInfo = TraceInfo.Create(message.TraceInfo);

		if (message.Message == null)
			return context.MessageHandlerResultFactory.FromResult(
				result.WithInvalidOperationException(traceInfo, $"{nameof(message)}.{nameof(message.Message)} == null"),
				traceInfo);

		var @event = message.Message;
		@event.Id = message.MessageId;

		var orchestrationRepository = context.ServiceProvider?.GetRequiredService<IOrchestrationRepository>();
		if (orchestrationRepository == null)
			return context.MessageHandlerResultFactory.FromResult(
				result.WithInvalidOperationException(traceInfo, $"{nameof(orchestrationRepository)} == null"),
				traceInfo);

		var saveResult = await orchestrationRepository.SaveNewEventAsync(@event, traceInfo, context.TransactionController, cancellationToken).ConfigureAwait(false);
		result.MergeErrors(saveResult);

		//var executionPointerFactory = context.ServiceProvider?.GetRequiredService<IExecutionPointerFactory>();
		//if (executionPointerFactory == null)
		//	return context.MessageHandlerResultFactory.FromResult(
		//		result.WithInvalidOperationException(traceInfo, $"{nameof(executionPointerFactory)} == null"),
		//		traceInfo);

		//var executionPointer = executionPointerFactory.BuildNextPointer(
		//	orchestrationInstance.OrchestrationDefinition,
		//	pointer,
		//	step.IdNextStep.Value);

		//if (executionPointer != null)
		//	await _orchestrationRepository.AddExecutionPointerAsync(orchestrationInstance.IdOrchestrationInstance, executionPointer).ConfigureAwait(false);


		var orchestrationInstances = await orchestrationRepository.GetOrchestrationInstancesAsync(@event.OrchestrationKey, context.ServiceProvider!, context.ServiceBusOptions!.HostInfo, context.TransactionController, default).ConfigureAwait(false);
		if (orchestrationInstances == null)
			return context.MessageHandlerResultFactory.FromResult(result.Build(), traceInfo);

		foreach (var instance in orchestrationInstances)
			if (instance.Status == OrchestrationStatus.Running || instance.Status == OrchestrationStatus.Executing)
				await instance.StartOrchestrationWorkerInternalAsync().ConfigureAwait(false);

		return context.MessageHandlerResultFactory.FromResult(result.Build(), traceInfo);
	}
}