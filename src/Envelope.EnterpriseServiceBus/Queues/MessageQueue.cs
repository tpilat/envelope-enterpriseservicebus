﻿using Envelope.Converters;
using Envelope.EnterpriseServiceBus.Internals;
using Envelope.EnterpriseServiceBus.MessageHandlers;
using Envelope.EnterpriseServiceBus.Messages;
using Envelope.EnterpriseServiceBus.Model;
using Envelope.EnterpriseServiceBus.Model.Internal;
using Envelope.EnterpriseServiceBus.Queues.Internal;
using Envelope.Extensions;
using Envelope.Infrastructure;
using Envelope.ServiceBus.Messages;
using Envelope.Services;
using Envelope.Threading;
using Envelope.Trace;
using Envelope.Transactions;
using Microsoft.Extensions.DependencyInjection;

namespace Envelope.EnterpriseServiceBus.Queues;

public class MessageQueue<TMessage> : IMessageQueue<TMessage>, IQueueInfo, IDisposable, IAsyncDisposable
	where TMessage : class, IMessage
{
	private readonly IQueue<IQueuedMessage<TMessage>> _queue;
	private readonly MessageQueueContext<TMessage> _messageQueueContext;
	private bool _disposed;

	/// <inheritdoc/>
	public Guid QueueId { get; }

	/// <inheritdoc/>
	public string QueueName { get; }

	/// <inheritdoc/>
	public bool IsPersistent => false;

	/// <inheritdoc/>
	public bool IsFaultQueue => false;

	/// <inheritdoc/>
	public QueueType QueueType { get; }

	public QueueStatus QueueStatus { get; private set; }

	/// <inheritdoc/>
	public bool IsPull { get; }

	/// <inheritdoc/>
	public int? MaxSize { get; }

	/// <inheritdoc/>
	public TimeSpan? DefaultProcessingTimeout { get; }

	/// <inheritdoc/>
	public TimeSpan FetchInterval { get; set; }

	public HandleMessage<TMessage>? MessageHandler { get; }

	protected virtual ITransactionController CreateTransactionController()
		=> _messageQueueContext.ServiceBusOptions.ServiceProvider.GetRequiredService<ITransactionCoordinator>().TransactionController;

	/// <inheritdoc/>
	public async Task<int> GetCountAsync(ITraceInfo traceInfo, CancellationToken cancellationToken = default)
	{
		if (traceInfo == null)
			throw new ArgumentNullException(nameof(traceInfo));

		var transactionController = CreateTransactionController();

		var count = await TransactionInterceptor.ExecuteAsync(
			true,
			traceInfo,
			transactionController,
			async (traceInfo, transactionController, unhandledExceptionDetail, cancellationToken) =>
			{
				var result = await _queue.GetCountAsync(traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
				if (result.HasError)
					throw result.ToException()!;

				return result.Data;
			},
			nameof(GetCountAsync),
			async (traceInfo, exception, detail) =>
			{
				await _messageQueueContext.ServiceBusOptions.HostLogger.LogErrorAsync(
					traceInfo,
					_messageQueueContext.ServiceBusOptions.HostInfo,
					x => x.ExceptionInfo(exception),
					$"{nameof(GetCountAsync)} dispose {nameof(transactionController)} error",
					null,
					cancellationToken: default).ConfigureAwait(false);
			},
			null,
			true,
			cancellationToken).ConfigureAwait(false);

		return count;
	}

	public MessageQueue(MessageQueueContext<TMessage> messageQueueContext)
	{
		_messageQueueContext = messageQueueContext ?? throw new ArgumentNullException(nameof(messageQueueContext));
		QueueName = _messageQueueContext.QueueName;
		QueueId = GuidConverter.ToGuid(QueueName);
		IsPull = _messageQueueContext.IsPull;
		QueueType = _messageQueueContext.QueueType;
		MaxSize = _messageQueueContext.MaxSize;

		if (QueueType == QueueType.Sequential_FIFO)
		{
			_queue = _messageQueueContext.FIFOQueue;
		}
		else if (QueueType == QueueType.Sequential_Delayable)
		{
			_queue = _messageQueueContext.DelayableQueue;
		}
		else
		{
			throw new NotImplementedException(QueueType.ToString());
		}

		DefaultProcessingTimeout = _messageQueueContext.DefaultProcessingTimeout;
		FetchInterval = _messageQueueContext.FetchInterval;
		MessageHandler = _messageQueueContext.MessageHandler;
	}

	/// <inheritdoc/>
	public async Task<IResult> EnqueueAsync(TMessage? message, IQueueEnqueueContext context, ITransactionController transactionController, CancellationToken cancellationToken)
	{
		var traceInfo = TraceInfo.Create(context.TraceInfo);
		var result = new ResultBuilder();

		if (_disposed)
			return result.WithInvalidOperationException(traceInfo, $"QueueName = {_messageQueueContext.QueueName}", new ObjectDisposedException(GetType().FullName));

		if (QueueStatus == QueueStatus.Terminated)
			return result.WithInvalidOperationException(traceInfo, $"{nameof(QueueStatus)} == {nameof(QueueStatus.Terminated)}");

		var queuedMessage = QueuedMessageFactory<TMessage>.CreateQueuedMessage(message, context, _messageQueueContext);

		var messagesMetadata = new List<IQueuedMessage<TMessage>> { queuedMessage };
		if (_messageQueueContext.MessageBodyProvider.AllowMessagePersistence(context.DisabledMessagePersistence, queuedMessage))
		{
			try
			{
				var saveResult =
					await _messageQueueContext.MessageBodyProvider.SaveToStorageAsync(
						messagesMetadata.Cast<IMessageMetadata>().ToList(),
						message,
						traceInfo,
						transactionController,
						cancellationToken).ConfigureAwait(false);

				if (result.MergeHasError(saveResult))
					return await PublishQueueEventAsync(
						queuedMessage,
						traceInfo,
						QueueEventType.Enqueue,
						result.Build()).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				return await PublishQueueEventAsync(
						queuedMessage,
						traceInfo,
						QueueEventType.Enqueue,
						result.WithInvalidOperationException(traceInfo, $"QueueName = {_messageQueueContext.QueueName}", ex)).ConfigureAwait(false);
			}
		}

		if (IsPull)
		{
			var enqueueResult = await _queue.EnqueueAsync(messagesMetadata, traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
			if (result.MergeHasError(enqueueResult))
				return await PublishQueueEventAsync(queuedMessage, traceInfo, QueueEventType.Enqueue, result.Build()).ConfigureAwait(false);
		}
		else //IsPush
		{
			if (MessageHandler == null)
				return await PublishQueueEventAsync(
						queuedMessage,
						traceInfo,
						QueueEventType.Enqueue,
						result.WithInvalidOperationException(traceInfo, $"{nameof(IsPull)} = {IsPull} | {nameof(MessageHandler)} == null")).ConfigureAwait(false);

			if (context.IsAsynchronousInvocation)
			{
				var enqueueResult = await _queue.EnqueueAsync(messagesMetadata, traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
				if (result.MergeHasError(enqueueResult))
					return await PublishQueueEventAsync(queuedMessage, traceInfo, QueueEventType.Enqueue, result.Build()).ConfigureAwait(false);
			}
			else //is synchronous invocation
			{
				var handlerResult = await HandleMessageAsync(queuedMessage, traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
				result.MergeErrors(handlerResult);
				if (handlerResult.Data?.Processed == false)
					result.WithError(traceInfo, x => x.InternalMessage(handlerResult.Data.ToString()));
			}

			if (!result.HasError())
				context.OnMessageQueueInternal = this;
		}

		return await PublishQueueEventAsync(queuedMessage, traceInfo, QueueEventType.Enqueue, result.Build()).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task<IResult> TryRemoveAsync(IQueuedMessage<TMessage> message, ITraceInfo traceInfo, ITransactionController transactionController, CancellationToken cancellationToken)
	{
		traceInfo = TraceInfo.Create(traceInfo);
		var result = new ResultBuilder();

		if (_disposed)
			return result.WithInvalidOperationException(traceInfo, $"QueueName = {_messageQueueContext.QueueName}", new ObjectDisposedException(GetType().FullName));

		if (IsPull)
			return await PublishQueueEventAsync(
				message,
				traceInfo,
				QueueEventType.Remove,
				(IResult)result.WithInvalidOperationException(traceInfo, $"QueueName = {_messageQueueContext.QueueName} | {nameof(TryRemoveAsync)}: {nameof(IsPull)} = {IsPull}")).ConfigureAwait(false);

		var removeResult = await _queue.TryRemoveAsync(message, traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
		result.MergeErrors(removeResult);
		return await PublishQueueEventAsync(
			message,
			traceInfo,
			QueueEventType.Remove,
			(IResult)result.Build()).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task<IResult<IQueuedMessage<TMessage>?>> TryPeekAsync(ITraceInfo traceInfo, ITransactionController transactionController, CancellationToken cancellationToken)
	{
		traceInfo = TraceInfo.Create(traceInfo);
		var result = new ResultBuilder<IQueuedMessage<TMessage>?>();

		if (_disposed)
			return result.WithInvalidOperationException(traceInfo, $"QueueName = {_messageQueueContext.QueueName}", new ObjectDisposedException(GetType().FullName));

		var peekResult = await _queue.TryPeekAsync(traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
		if (result.MergeHasError(peekResult))
			return await PublishQueueEventAsync(null, traceInfo, QueueEventType.Peek, result.Build()).ConfigureAwait(false);

		var messageHeader = peekResult.Data;

		if (messageHeader == null)
			return await PublishQueueEventAsync(
				messageHeader,
				traceInfo,
				QueueEventType.Peek,
				result.Build()).ConfigureAwait(false);

		if (messageHeader is not IQueuedMessage<TMessage> queuedMessage)
			return await PublishQueueEventAsync(
					messageHeader,
					traceInfo,
					QueueEventType.Peek,
					result.WithInvalidOperationException(
						traceInfo,
						$"QueueName = {_messageQueueContext.QueueName} | {nameof(_queue)} must by type of {typeof(IQueuedMessage<TMessage>).FullName} but {messageHeader.GetType().FullName} found.")).ConfigureAwait(false);

		if (QueueType == QueueType.Sequential_FIFO && (queuedMessage.MessageStatus == MessageStatus.Suspended || queuedMessage.MessageStatus == MessageStatus.Aborted))
		{
			QueueStatus = QueueStatus.Suspended;
			//if (!IsPull)
			//{
				return await PublishQueueEventAsync(
					null,
					traceInfo,
					QueueEventType.Peek,
					result.Build()).ConfigureAwait(false);
			//}
		}

		if (_messageQueueContext.MessageBodyProvider.AllowMessagePersistence(queuedMessage.DisabledMessagePersistence, queuedMessage))
		{
			try
			{
				var loadResult = await _messageQueueContext.MessageBodyProvider.LoadFromStorageAsync<TMessage>(queuedMessage, traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
				if (result.MergeHasError(loadResult))
					return await PublishQueueEventAsync(messageHeader, traceInfo, QueueEventType.Peek, result.Build()).ConfigureAwait(false);

				var message = loadResult.Data;

				//kedze plati ContainsContent = true
				if (message == null)
					return await PublishQueueEventAsync(
						messageHeader,
						traceInfo,
						QueueEventType.Peek,
						result.WithInvalidOperationException(
							traceInfo,
							$"QueueName = {_messageQueueContext.QueueName} | {nameof(TryPeekAsync)}: {nameof(queuedMessage.QueueName)} == {queuedMessage.QueueName} | {nameof(queuedMessage.MessageId)} == {queuedMessage.MessageId} | {nameof(message)} == null")).ConfigureAwait(false);

				queuedMessage.SetMessageInternal(message);
			}
			catch (Exception ex)
			{
				return await PublishQueueEventAsync(
					messageHeader,
					traceInfo,
					QueueEventType.Peek,
					result.WithInvalidOperationException(traceInfo, $"QueueName = {_messageQueueContext.QueueName}", ex)).ConfigureAwait(false);
			}
		}

		return await PublishQueueEventAsync(
			messageHeader,
			traceInfo,
			QueueEventType.Peek,
			result.WithData(queuedMessage).Build()).ConfigureAwait(false);
	}

	Task IMessageQueue.OnMessageInternalAsync(ITraceInfo traceInfo, CancellationToken cancellationToken)
		=> OnMessageAsync(traceInfo, cancellationToken);

	private readonly AsyncLock _onMessageLock = new();
	private async Task OnMessageAsync(ITraceInfo traceInfo, CancellationToken cancellationToken)
	{
		if (_disposed)
			return;

		traceInfo = TraceInfo.Create(traceInfo);

		using (await _onMessageLock.LockAsync().ConfigureAwait(false))
		{
			if (_disposed)
				return;

			while (0 < (await GetCountAsync(traceInfo, cancellationToken).ConfigureAwait(false)))
			{
				if (cancellationToken.IsCancellationRequested)
					return;

				var transactionController = CreateTransactionController();

				IQueuedMessage<TMessage>? message = null;

				var loopControl = await TransactionInterceptor.ExecuteAsync(
					false,
					traceInfo,
					transactionController,
					//$"{nameof(message.QueueName)} == {message?.QueueName} | {nameof(message.SourceExchangeName)} == {message?.SourceExchangeName} | MessageType = {message?.Message?.GetType().FullName}"
					async (traceInfo, transactionController, unhandledExceptionDetail, cancellationToken) =>
					{
						var peekResult = await TryPeekAsync(traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
						if (peekResult.HasError)
						{
							await _messageQueueContext.ServiceBusOptions.HostLogger.LogResultErrorMessagesAsync(_messageQueueContext.ServiceBusOptions.HostInfo, peekResult, null, cancellationToken).ConfigureAwait(false);
							transactionController.ScheduleRollback(nameof(TryPeekAsync));

							return LoopControlEnum.Return;
						}

						message = peekResult.Data;

						if (message != null)
						{
							if (message.Processed)
							{
								var removeResult = await _queue.TryRemoveAsync(message, traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
								if (removeResult.HasError)
								{
									transactionController.ScheduleRollback(nameof(_queue.TryRemoveAsync));

									await _messageQueueContext.ServiceBusOptions.HostLogger.LogResultErrorMessagesAsync(_messageQueueContext.ServiceBusOptions.HostInfo, removeResult, null, cancellationToken).ConfigureAwait(false);
									await PublishQueueEventAsync(message, traceInfo, QueueEventType.OnMessage, removeResult).ConfigureAwait(false);
								}
								else
								{
									transactionController.ScheduleCommit();
								}

								return LoopControlEnum.Continue;
							}

							var nowUtc = DateTime.UtcNow;
							if (message.TimeToLiveUtc < nowUtc)
							{
								if (!message.DisableFaultQueue)
								{
									try
									{
										var faultContext = _messageQueueContext.ServiceBusOptions.QueueProvider.CreateFaultQueueContext(traceInfo, message);
										var enqueueResult = await _messageQueueContext.ServiceBusOptions.QueueProvider.FaultQueue.EnqueueAsync(message.Message, faultContext, transactionController, cancellationToken).ConfigureAwait(false);

										if (enqueueResult.HasError)
										{
											transactionController.ScheduleRollback($"{nameof(_messageQueueContext.ServiceBusOptions.ExchangeProvider.FaultQueue)}.{nameof(_messageQueueContext.ServiceBusOptions.QueueProvider.FaultQueue.EnqueueAsync)}");
										}
										else
										{
											transactionController.ScheduleCommit();
										}
									}
									catch (Exception faultEx)
									{
										await _messageQueueContext.ServiceBusOptions.HostLogger.LogErrorAsync(
											traceInfo,
											_messageQueueContext.ServiceBusOptions.HostInfo,
											x => x
												.ExceptionInfo(faultEx)
												.Detail($"{nameof(message.QueueName)} == {message.QueueName} | {nameof(message.SourceExchangeName)} == {message.SourceExchangeName} | MessageType = {message.Message?.GetType().FullName} >> {nameof(_messageQueueContext.ServiceBusOptions.QueueProvider.FaultQueue)}.{nameof(_messageQueueContext.ServiceBusOptions.QueueProvider.FaultQueue.EnqueueAsync)}"),
											$"{nameof(OnMessageAsync)} >> {nameof(_messageQueueContext.ServiceBusOptions.QueueProvider.FaultQueue)}",
											null,
											cancellationToken: default).ConfigureAwait(false);
									}
								}

								return LoopControlEnum.Continue;
							}

							var handlerResult = await HandleMessageAsync(message, traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
							if (handlerResult.Data!.Processed)
							{
								var removeResult = await _queue.TryRemoveAsync(message, traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
								if (removeResult.HasError)
								{
									transactionController.ScheduleRollback($"{nameof(HandleMessageAsync)} - {nameof(_queue.TryRemoveAsync)}");

									await _messageQueueContext.ServiceBusOptions.HostLogger.LogResultErrorMessagesAsync(_messageQueueContext.ServiceBusOptions.HostInfo, removeResult, null, cancellationToken).ConfigureAwait(false);
									await PublishQueueEventAsync(message, traceInfo, QueueEventType.OnMessage, removeResult).ConfigureAwait(false);
								}
								else
								{
									transactionController.ScheduleCommit();
								}
							}
						}

						return LoopControlEnum.None;
					},
					$"{nameof(OnMessageAsync)} {nameof(message.Processed)} = {message?.Processed}",
					async (traceInfo, exception, detail) =>
					{
						await _messageQueueContext.ServiceBusOptions.HostLogger.LogErrorAsync(
						traceInfo,
						_messageQueueContext.ServiceBusOptions.HostInfo,
						x => x.ExceptionInfo(exception).Detail(detail),
						detail,
						null,
						cancellationToken: default).ConfigureAwait(false);

						await PublishQueueEventAsync(
							message,
							traceInfo,
							QueueEventType.OnMessage,
							new ResultBuilder().WithInvalidOperationException(traceInfo, "Global exception!", exception)).ConfigureAwait(false);
					},
					null,
					true,
					cancellationToken).ConfigureAwait(false);

				if (loopControl == LoopControlEnum.Break || loopControl == LoopControlEnum.Return)
					break;
			}
		}
	}

	private async Task<IResult<IMessageMetadataUpdate>> HandleMessageAsync(IQueuedMessage<TMessage> message, ITraceInfo traceInfo, ITransactionController transactionController, CancellationToken cancellationToken)
	{
		traceInfo = TraceInfo.Create(traceInfo);
		var result = new ResultBuilder<IMessageMetadataUpdate>();

		var processingTimeout = DefaultProcessingTimeout;

		var handlerContext = _messageQueueContext.ServiceBusOptions.MessageHandlerContextFactory(_messageQueueContext.ServiceBusOptions.ServiceProvider);

		if (handlerContext == null)
			return result.WithInvalidOperationException(traceInfo, $"{nameof(handlerContext)} == null");

		handlerContext.Initialize(new MessageHandlerContextOptions
		{
			ServiceBusOptions = _messageQueueContext.ServiceBusOptions,
			MessageHandlerResultFactory = _messageQueueContext.ServiceBusOptions.MessageHandlerResultFactory,
			TransactionController = transactionController,
			ServiceProvider = _messageQueueContext.ServiceBusOptions.ServiceProvider,
			TraceInfo = TraceInfo.Create(traceInfo),
			HostInfo = _messageQueueContext.ServiceBusOptions.HostInfo,
			HandlerLogger = _messageQueueContext.ServiceBusOptions.HandlerLogger,
			MessageId = message.MessageId,
			DisabledMessagePersistence = message.DisabledMessagePersistence,
			ThrowNoHandlerException = true,
			PublisherId = PublisherHelper.GetPublisherIdentifier(_messageQueueContext.ServiceBusOptions.HostInfo, traceInfo),
			PublishingTimeUtc = message.PublishingTimeUtc,
			ParentMessageId = message.ParentMessageId,
			Timeout = message.Timeout,
			RetryCount = message.RetryCount,
			ErrorHandling = message.ErrorHandling,
			IdSession = message.IdSession,
			ContentType = message.ContentType,
			ContentEncoding = message.ContentEncoding,
			IsCompressedContent = message.IsCompressedContent,
			IsEncryptedContent = message.IsEncryptedContent,
			ContainsContent = message.ContainsContent,
			HasSelfContent = message.HasSelfContent,
			Priority = message.Priority,
			Headers = message.Headers
		},
		message.MessageStatus,
		message.DelayedToUtc);

		var task = MessageHandler!(message, handlerContext, cancellationToken);
		if (processingTimeout.HasValue)
			task = task.OrTimeoutAsync(processingTimeout.Value);

		MessageHandlerResult? handlerResult = null;

		try
		{
			handlerResult = await task.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			result.WithInvalidOperationException(traceInfo, $"{nameof(handlerResult)} == null", ex);
			await _messageQueueContext.ServiceBusOptions.HostLogger.LogResultErrorMessagesAsync(_messageQueueContext.ServiceBusOptions.HostInfo, result.Build(), null, cancellationToken).ConfigureAwait(false);
		}

		if (handlerResult == null)
		{
			result.WithInvalidOperationException(traceInfo, $"{nameof(handlerResult)} == null");

			await _messageQueueContext.ServiceBusOptions.HostLogger.LogResultErrorMessagesAsync(_messageQueueContext.ServiceBusOptions.HostInfo, result.Build(), null, cancellationToken).ConfigureAwait(false);
			return await PublishQueueEventAsync(message, traceInfo, QueueEventType.OnMessage, result.Build()).ConfigureAwait(false);
		}

		var hasError = handlerResult.ErrorResult?.HasError == true;
		if (hasError)
		{
			result.MergeAll(handlerResult.ErrorResult!);
			await _messageQueueContext.ServiceBusOptions.HostLogger.LogResultErrorMessagesAsync(_messageQueueContext.ServiceBusOptions.HostInfo, result.Build(), null, cancellationToken).ConfigureAwait(false);
			await PublishQueueEventAsync(message, traceInfo, QueueEventType.OnMessage, result.Build()).ConfigureAwait(false);
		}

		var update = new MessageMetadataUpdate(message.MessageId)
		{
			MessageStatus = handlerResult.MessageStatus
		};

		if (update.MessageStatus != MessageStatus.Completed)
		{
			if (handlerResult.Retry)
			{
				var errorController = message.ErrorHandling ?? _messageQueueContext.ErrorHandling;
				var canRetry = errorController?.CanRetry(message.RetryCount);

				var retryed = false;
				if (canRetry == true)
				{
					var retryInterval = handlerResult.RetryInterval ?? errorController!.GetRetryTimeSpan(message.RetryCount);
					if (retryInterval.HasValue)
					{
						retryed = true;
						update.RetryCount = message.RetryCount + 1;
						update.DelayedToUtc = handlerResult.GetDelayedToUtc(retryInterval.Value);
					}
				}

				if (!retryed)
					update.MessageStatus = MessageStatus.Suspended;
			}
			else if (update.MessageStatus == MessageStatus.Deferred && handlerResult.RetryInterval.HasValue)
			{
				update.DelayedToUtc = handlerResult.GetDelayedToUtc(handlerResult.RetryInterval.Value);
			}
		}

		update.Processed = update.MessageStatus == MessageStatus.Completed;

		if (QueueType == QueueType.Sequential_FIFO && (update.MessageStatus == MessageStatus.Suspended || update.MessageStatus == MessageStatus.Aborted) && QueueStatus != QueueStatus.Terminated)
			QueueStatus = QueueStatus.Suspended;

		var localTransactionController = CreateTransactionController();

		var queueStatus = await TransactionInterceptor.ExecuteAsync(
			false,
			traceInfo,
			localTransactionController,
			async (traceInfo, transactionController, unhandledExceptionDetail, cancellationToken) =>
			{
				var updateResult = await _queue.UpdateAsync(message, update, traceInfo, transactionController, cancellationToken).ConfigureAwait(false);
				if (updateResult.HasTransactionRollbackError)
				{
					transactionController.ScheduleRollback();
				}
				else
				{
					transactionController.ScheduleCommit();
				}

				return updateResult.Data;
			},
			$"{nameof(HandleMessageAsync)} {nameof(_queue.UpdateAsync)} | {nameof(message.MessageId)} = {message.MessageId}",
			(traceInfo, exception, detail) =>
			{
				return _messageQueueContext.ServiceBusOptions.HostLogger.LogErrorAsync(
					traceInfo,
					_messageQueueContext.ServiceBusOptions.HostInfo,
					x => x.ExceptionInfo(exception).Detail(detail),
					detail,
					null,
					cancellationToken: default);
			},
			null,
			true,
			cancellationToken).ConfigureAwait(false);

		if (QueueStatus == QueueStatus.Running)
			QueueStatus = queueStatus;
		else if (queueStatus == QueueStatus.Terminated)
			QueueStatus = queueStatus;

		return await PublishQueueEventAsync(
			message,
			traceInfo,
			QueueEventType.OnMessage,
			result.WithData(update).Build()).ConfigureAwait(false);
	}

	private async Task<TResult> PublishQueueEventAsync<TResult>(IMessageMetadata? message, ITraceInfo traceInfo, QueueEventType queueEventType, TResult result)
		where TResult : IResult
	{
		IQueueEvent queueEvent;
		if (result.HasError)
		{
			queueEvent = new QueueErrorEvent(this, queueEventType, message, result);
			await _messageQueueContext.ServiceBusOptions.HostLogger.LogResultErrorMessagesAsync(_messageQueueContext.ServiceBusOptions.HostInfo, result, null, cancellationToken: default).ConfigureAwait(false);
		}
		else
		{
			queueEvent = new QueueEvent(this, queueEventType, message);
			await _messageQueueContext.ServiceBusOptions.HostLogger.LogResultAllMessagesAsync(_messageQueueContext.ServiceBusOptions.HostInfo, result, null, cancellationToken: default).ConfigureAwait(false);
		}

		await _messageQueueContext.ServiceBusOptions.ServiceBusLifeCycleEventManager.PublishServiceBusEventInternalAsync(
			queueEvent,
			traceInfo,
			_messageQueueContext.ServiceBusOptions).ConfigureAwait(false);

		return result!;
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
			return;

		_disposed = true;

		await DisposeAsyncCoreAsync().ConfigureAwait(false);

		Dispose(disposing: false);
		GC.SuppressFinalize(this);
	}

	protected virtual ValueTask DisposeAsyncCoreAsync()
	{
		_queue.Dispose();
		return ValueTask.CompletedTask;
	}

	protected virtual void Dispose(bool disposing)
	{
		if (_disposed)
			return;

		_disposed = true;

		if (disposing)
			_queue.Dispose();
	}

	public void Dispose()
	{
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}
}
