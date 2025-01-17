﻿using Envelope.ServiceBus.Hosts;
using Envelope.EnterpriseServiceBus.Internals;
using Envelope.EnterpriseServiceBus.MessageHandlers;
using Envelope.EnterpriseServiceBus.MessageHandlers.Processors;
using Envelope.EnterpriseServiceBus.Messages;
using Envelope.EnterpriseServiceBus.Messages.Options;
using Envelope.ServiceBus.Messages;
using Envelope.Services;
using Envelope.Services.Transactions;
using Envelope.Trace;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace Envelope.EnterpriseServiceBus;

public partial class MessageBus : IMessageBus
{
	public IResult Send(
		IRequestMessage message,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
		=> SendWithMessageId(message, null!, memberName, sourceFilePath, sourceLineNumber);

	public IResult Send(
		IRequestMessage message,
		Action<MessageOptionsBuilder> optionsBuilder,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
		=> SendWithMessageId(
			message,
			optionsBuilder,
			TraceInfo.Create(
				ServiceProvider.GetRequiredService<IApplicationContext>().TraceInfo,
				null, //MessageBusOptions.HostInfo.HostName,
				null,
				memberName,
				sourceFilePath,
				sourceLineNumber));

	public IResult Send(
		IRequestMessage message,
		ITraceInfo traceInfo)
		=> SendWithMessageId(message, (Action<MessageOptionsBuilder>?)null, traceInfo);

	public IResult Send(
		IRequestMessage message,
		Action<MessageOptionsBuilder>? optionsBuilder,
		ITraceInfo traceInfo)
		=> SendWithMessageId(message, optionsBuilder, traceInfo);

	public IResult<Guid> SendWithMessageId(
		IRequestMessage message,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
		=> SendWithMessageId(message, null!, memberName, sourceFilePath, sourceLineNumber);

	public IResult<Guid> SendWithMessageId(
		IRequestMessage message,
		Action<MessageOptionsBuilder> optionsBuilder,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
		=> SendWithMessageId(
			message,
			optionsBuilder,
			TraceInfo.Create(
				ServiceProvider.GetRequiredService<IApplicationContext>().TraceInfo,
				null, //MessageBusOptions.HostInfo.HostName,
				null,
				memberName,
				sourceFilePath,
				sourceLineNumber));

	public IResult<Guid> SendWithMessageId(
		IRequestMessage message,
		ITraceInfo traceInfo)
		=> SendWithMessageId(message, (Action<MessageOptionsBuilder>?)null, traceInfo);

	public IResult<Guid> SendWithMessageId(
		IRequestMessage message,
		Action<MessageOptionsBuilder>? optionsBuilder,
		ITraceInfo traceInfo)
	{
		if (message == null)
		{
			var result = new ResultBuilder<Guid>();
			return result.WithArgumentNullException(traceInfo, nameof(message));
		}

		var builder = MessageOptionsBuilder.GetDefaultBuilder(message.GetType());
		optionsBuilder?.Invoke(builder);
		var options = builder.Build(true);

		var isLocalTransactionCoordinator = false;
		if (options.TransactionController == null)
		{
			options.TransactionController = CreateTransactionController();
			isLocalTransactionCoordinator = true;
		}

		return SendInternal(message, options, isLocalTransactionCoordinator, traceInfo);
	}

	protected IResult<Guid> SendInternal(
		IRequestMessage message,
		IMessageOptions options,
		bool isLocalTransactionCoordinator,
		ITraceInfo traceInfo)
	{
		var result = new ResultBuilder<Guid>();

		if (message == null)
			return result.WithArgumentNullException(traceInfo, nameof(message));
		if (options == null)
			return result.WithArgumentNullException(traceInfo, nameof(options));
		if (traceInfo == null)
			return result.WithArgumentNullException(
				TraceInfo.Create(
					ServiceProvider.GetRequiredService<IApplicationContext>().TraceInfo
					//MessageBusOptions.HostInfo.HostName
					),
				nameof(traceInfo));

		traceInfo = TraceInfo.Create(traceInfo);

		var transactionController = options.TransactionController;
		VoidMessageHandlerProcessor? handlerProcessor = null;
		IMessageHandlerContext? handlerContext = null;

		return ServiceTransactionInterceptor.ExecuteAction(
			false,
			traceInfo,
			transactionController,
			(traceInfo, transactionController, unhandledExceptionDetail) =>
			{
				var requestMessageType = message.GetType();

				var savedMessageResult = SaveRequestMessage(message, options, traceInfo);
				if (result.MergeHasError(savedMessageResult))
					return result.Build();

				var savedMessage = savedMessageResult.Data;

				if (savedMessage == null)
					return result.WithInvalidOperationException(traceInfo, $"{nameof(savedMessage)} == null | {nameof(requestMessageType)} = {requestMessageType.FullName}");
				if (savedMessage.Message == null)
					return result.WithInvalidOperationException(traceInfo, $"{nameof(savedMessage)}.{nameof(savedMessage.Message)} == null | {nameof(requestMessageType)} = {requestMessageType.FullName}");

				handlerContext = MessageHandlerRegistry.CreateMessageHandlerContext(requestMessageType, ServiceProvider);

				if (handlerContext == null)
					return result.WithInvalidOperationException(traceInfo, $"{nameof(handlerContext)} == null| {nameof(requestMessageType)} = {requestMessageType.FullName}");

				handlerContext.Initialize(new MessageHandlerContextOptions
				{
					MessageHandlerResultFactory = MessageBusOptions.MessageHandlerResultFactory,
					TransactionController = transactionController,
					ServiceProvider = ServiceProvider,
					TraceInfo = traceInfo,
					HostInfo = MessageBusOptions.HostInfo,
					HandlerLogger = MessageBusOptions.HandlerLogger,
					MessageId = savedMessage.MessageId,
					DisabledMessagePersistence = options.DisabledMessagePersistence,
					ThrowNoHandlerException = true,
					PublisherId = PublisherHelper.GetPublisherIdentifier(MessageBusOptions.HostInfo, traceInfo),
					PublishingTimeUtc = DateTime.UtcNow,
					ParentMessageId = null,
					Timeout = options.Timeout,
					RetryCount = 0,
					ErrorHandling = options.ErrorHandling,
					IdSession = options.IdSession,
					ContentType = options.ContentType,
					ContentEncoding = options.ContentEncoding,
					IsCompressedContent = options.IsCompressContent,
					IsEncryptedContent = options.IsEncryptContent,
					ContainsContent = true,
					Priority = options.Priority,
					Headers = options.Headers?.GetAll()
				},
				MessageStatus.Created,
				null);

				handlerProcessor = (VoidMessageHandlerProcessor)_asyncVoidMessageHandlerProcessors.GetOrAdd(
					requestMessageType,
					requestMessageType =>
					{
						var processor = Activator.CreateInstance(typeof(VoidMessageHandlerProcessor<,>).MakeGenericType(requestMessageType, handlerContext.GetType())) as MessageHandlerProcessorBase;

						if (processor == null)
							result.WithInvalidOperationException(traceInfo, $"Could not create handlerProcessor type for {requestMessageType}");

						return processor!;
					});

				if (result.HasError())
					return result.Build();

				if (handlerProcessor == null)
					return result.WithInvalidOperationException(traceInfo, $"Could not create handlerProcessor type for {requestMessageType}");

				var handlerResult = handlerProcessor.Handle(savedMessage.Message, handlerContext, ServiceProvider, unhandledExceptionDetail);
				result.MergeAll(handlerResult);

				if (result.HasTransactionRollbackError())
				{
					transactionController.ScheduleRollback();
				}
				else
				{
					if (isLocalTransactionCoordinator)
						transactionController.ScheduleCommit();
				}

				return result.WithData(savedMessage.MessageId).Build();
			},
			$"{nameof(Send)}<{message?.GetType().FullName}> return {typeof(IResult<Guid>).FullName}",
			(traceInfo, exception, detail) =>
			{
				var errorMessage =
					MessageBusOptions.HostLogger.LogError(
						traceInfo,
						MessageBusOptions.HostInfo,
						x => x.ExceptionInfo(exception).Detail(detail),
						detail,
						null);

				if (handlerProcessor != null)
				{
					try
					{
						handlerProcessor.OnError(traceInfo, exception, null, detail, message, handlerContext, ServiceProvider);
					}
					catch { }
				}

				return errorMessage;
			},
			null,
			isLocalTransactionCoordinator);
	}

	public IResult<TResponse> Send<TResponse>(
		IRequestMessage<TResponse> message,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
		=> Send(message, null!, memberName, sourceFilePath, sourceLineNumber);

	public IResult<TResponse> Send<TResponse>(
		IRequestMessage<TResponse> message,
		Action<MessageOptionsBuilder> optionsBuilder,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
		=> Send(
			message,
			optionsBuilder,
			TraceInfo.Create(
				ServiceProvider.GetRequiredService<IApplicationContext>().TraceInfo,
				null, //MessageBusOptions.HostInfo.HostName,
				null,
				memberName,
				sourceFilePath,
				sourceLineNumber));

	public IResult<TResponse> Send<TResponse>(
		IRequestMessage<TResponse> message,
		ITraceInfo traceInfo)
		=> Send(message, null, traceInfo);

	public IResult<TResponse> Send<TResponse>(
		IRequestMessage<TResponse> message,
		Action<MessageOptionsBuilder>? optionsBuilder,
		ITraceInfo traceInfo)
	{
		var result = new ResultBuilder<TResponse>();
		if (message == null)
			return result.WithArgumentNullException(traceInfo, nameof(message));

		var builder = MessageOptionsBuilder.GetDefaultBuilder(message.GetType());
		optionsBuilder?.Invoke(builder);
		var options = builder.Build(true);

		var isLocalTransactionCoordinator = false;
		if (options.TransactionController == null)
		{
			options.TransactionController = CreateTransactionController();
			isLocalTransactionCoordinator = true;
		}

		var sendResult = SendInternal(message, options, isLocalTransactionCoordinator, traceInfo);
		result.MergeAll(sendResult);

		if (sendResult.Data != null)
			result.WithData(sendResult.Data.Response);

		return result.Build();
	}

	public IResult<ISendResponse<TResponse>> SendWithMessageId<TResponse>(
		IRequestMessage<TResponse> message,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
		=> SendWithMessageId(message, null!, memberName, sourceFilePath, sourceLineNumber);

	public IResult<ISendResponse<TResponse>> SendWithMessageId<TResponse>(
		IRequestMessage<TResponse> message,
		Action<MessageOptionsBuilder> optionsBuilder,
		[CallerMemberName] string memberName = "",
		[CallerFilePath] string sourceFilePath = "",
		[CallerLineNumber] int sourceLineNumber = 0)
		=> SendWithMessageId(
			message,
			optionsBuilder,
			TraceInfo.Create(
				ServiceProvider.GetRequiredService<IApplicationContext>().TraceInfo,
				null, //MessageBusOptions.HostInfo.HostName,
				null,
				memberName,
				sourceFilePath,
				sourceLineNumber));

	public IResult<ISendResponse<TResponse>> SendWithMessageId<TResponse>(
		IRequestMessage<TResponse> message,
		ITraceInfo traceInfo)
		=> SendWithMessageId(message, null, traceInfo);

	public IResult<ISendResponse<TResponse>> SendWithMessageId<TResponse>(
		IRequestMessage<TResponse> message,
		Action<MessageOptionsBuilder>? optionsBuilder,
		ITraceInfo traceInfo)
	{
		if (message == null)
		{
			var result = new ResultBuilder<ISendResponse<TResponse>>();
			return result.WithArgumentNullException(traceInfo, nameof(message));
		}

		var builder = MessageOptionsBuilder.GetDefaultBuilder(message.GetType());
		optionsBuilder?.Invoke(builder);
		var options = builder.Build(true);

		var isLocalTransactionCoordinator = false;
		if (options.TransactionController == null)
		{
			options.TransactionController = CreateTransactionController();
			isLocalTransactionCoordinator = true;
		}

		return SendInternal(message, options, isLocalTransactionCoordinator, traceInfo);
	}

	protected IResult<ISendResponse<TResponse>> SendInternal<TResponse>(
		IRequestMessage<TResponse> message,
		IMessageOptions options,
		bool isLocalTransactionCoordinator,
		ITraceInfo traceInfo)
	{
		var result = new ResultBuilder<ISendResponse<TResponse>>();

		if (message == null)
			return result.WithArgumentNullException(traceInfo, nameof(message));
		if (options == null)
			return result.WithArgumentNullException(traceInfo, nameof(options));
		if (traceInfo == null)
			return result.WithArgumentNullException(
				TraceInfo.Create(
					ServiceProvider.GetRequiredService<IApplicationContext>().TraceInfo
					//MessageBusOptions.HostInfo.HostName
					),
				nameof(traceInfo));

		traceInfo = TraceInfo.Create(traceInfo);

		var transactionController = options.TransactionController;
		MessageHandlerProcessor<TResponse>? handlerProcessor = null;
		IMessageHandlerContext? handlerContext = null;

		return ServiceTransactionInterceptor.ExecuteAction(
			false,
			traceInfo,
			transactionController,
			(traceInfo, transactionController, unhandledExceptionDetail) =>
			{
				var requestMessageType = message.GetType();

				var savedMessageResult = SaveRequestMessage<IRequestMessage<TResponse>, TResponse>(message, options, traceInfo);
				if (result.MergeHasError(savedMessageResult))
					return result.Build();

				var savedMessage = savedMessageResult.Data;

				if (savedMessage == null)
					return result.WithInvalidOperationException(traceInfo, $"{nameof(savedMessage)} == null | {nameof(requestMessageType)} = {requestMessageType.FullName}");
				if (savedMessage.Message == null)
					return result.WithInvalidOperationException(traceInfo, $"{nameof(savedMessage)}.{nameof(savedMessage.Message)} == null | {nameof(requestMessageType)} = {requestMessageType.FullName}");

				handlerContext = MessageHandlerRegistry.CreateMessageHandlerContext(requestMessageType, ServiceProvider);

				if (handlerContext == null)
					return result.WithInvalidOperationException(traceInfo, $"{nameof(handlerContext)} == null| {nameof(requestMessageType)} = {requestMessageType.FullName}");

				handlerContext.Initialize(new MessageHandlerContextOptions
				{
					MessageHandlerResultFactory = MessageBusOptions.MessageHandlerResultFactory,
					TransactionController = transactionController,
					ServiceProvider = ServiceProvider,
					TraceInfo = traceInfo,
					HostInfo = MessageBusOptions.HostInfo,
					HandlerLogger = MessageBusOptions.HandlerLogger,
					MessageId = savedMessage.MessageId,
					DisabledMessagePersistence = options.DisabledMessagePersistence,
					ThrowNoHandlerException = true,
					PublisherId = PublisherHelper.GetPublisherIdentifier(MessageBusOptions.HostInfo, traceInfo),
					PublishingTimeUtc = DateTime.UtcNow,
					ParentMessageId = null,
					Timeout = options.Timeout,
					RetryCount = 0,
					ErrorHandling = options.ErrorHandling,
					IdSession = options.IdSession,
					ContentType = options.ContentType,
					ContentEncoding = options.ContentEncoding,
					IsCompressedContent = options.IsCompressContent,
					IsEncryptedContent = options.IsEncryptContent,
					ContainsContent = true,
					Priority = options.Priority,
					Headers = options.Headers?.GetAll()
				},
				MessageStatus.Created,
				null);

				handlerProcessor = (MessageHandlerProcessor<TResponse>)_asyncVoidMessageHandlerProcessors.GetOrAdd(
					requestMessageType,
					requestMessageType =>
					{
						var processor = Activator.CreateInstance(typeof(MessageHandlerProcessor<,,>).MakeGenericType(requestMessageType, typeof(TResponse), handlerContext.GetType())) as MessageHandlerProcessorBase;

						if (processor == null)
							result.WithInvalidOperationException(traceInfo, $"Could not create handlerProcessor type for {requestMessageType}");

						return processor!;
					});

				if (result.HasError())
					return result.Build();

				if (handlerProcessor == null)
					return result.WithInvalidOperationException(traceInfo, $"Could not create handlerProcessor type for {requestMessageType}");

				var handlerResult = handlerProcessor.Handle(savedMessage.Message, handlerContext, ServiceProvider, traceInfo, SaveResponseMessage, unhandledExceptionDetail);
				result.MergeAllWithData(handlerResult);

				if (result.HasTransactionRollbackError())
				{
					transactionController.ScheduleRollback();
				}
				else
				{
					if (isLocalTransactionCoordinator)
						transactionController.ScheduleCommit();
				}

				return result.WithData(handlerResult.Data).Build();
			},
			$"{nameof(Send)}<{message?.GetType().FullName}> return {typeof(IResult<ISendResponse<TResponse>>).FullName}",
			(traceInfo, exception, detail) =>
			{
				var errorMessage =
					MessageBusOptions.HostLogger.LogError(
						traceInfo,
						MessageBusOptions.HostInfo,
						x => x.ExceptionInfo(exception).Detail(detail),
						detail,
						null);

				if (handlerProcessor != null)
				{
					try
					{
						handlerProcessor.OnError(traceInfo, exception, null, detail, message, handlerContext, ServiceProvider);
					}
					catch { }
				}

				return errorMessage;
			},
			null,
			isLocalTransactionCoordinator);
	}

	protected virtual IResult<ISavedMessage<TMessage>> SaveRequestMessage<TMessage, TResponse>(
		TMessage requestMessage,
		IMessageOptions options,
		ITraceInfo traceInfo)
		where TMessage : class, IRequestMessage<TResponse>
	{
		traceInfo = TraceInfo.Create(traceInfo);
		var result = new ResultBuilder<ISavedMessage<TMessage>>();

		var utcNow = DateTime.UtcNow;
		var metadata = new MessageMetadata<TMessage>
		{
			MessageId = Guid.NewGuid(),
			Message = requestMessage,
			ParentMessageId = null,
			PublishingTimeUtc = utcNow,
			PublisherId = "--MessageBus--",
			TraceInfo = traceInfo,
			Timeout = options.Timeout,
			RetryCount = 0,
			ErrorHandling = options.ErrorHandling,
			IdSession = options.IdSession,
			ContentType = options.ContentType,
			ContentEncoding = options.ContentEncoding,
			IsCompressedContent = options.IsCompressContent,
			IsEncryptedContent = options.IsEncryptContent,
			ContainsContent = requestMessage != null,
			Priority = options.Priority,
			Headers = options.Headers?.GetAll(),
			DisabledMessagePersistence = options.DisabledMessagePersistence,
			MessageStatus = MessageStatus.Created,
			DelayedToUtc = null
		};

		if (MessageBusOptions.MessageBodyProvider != null
			&& MessageBusOptions.MessageBodyProvider.AllowMessagePersistence(options.DisabledMessagePersistence, metadata))
		{
			var saveResult = MessageBusOptions.MessageBodyProvider.SaveToStorage(new List<IMessageMetadata> { metadata }, requestMessage, traceInfo, options.TransactionController);
			if (result.MergeHasError(saveResult))
				return result.Build();
		}

		return result.WithData(metadata).Build();
	}

	protected virtual IResult<ISavedMessage<TMessage>> SaveRequestMessage<TMessage>(
		TMessage requestMessage,
		IMessageOptions options,
		ITraceInfo traceInfo)
		where TMessage : class, IRequestMessage
	{
		traceInfo = TraceInfo.Create(traceInfo);
		var result = new ResultBuilder<ISavedMessage<TMessage>>();

		var utcNow = DateTime.UtcNow;
		var metadata = new MessageMetadata<TMessage>
		{
			MessageId = Guid.NewGuid(),
			Message = requestMessage,
			ParentMessageId = null,
			PublishingTimeUtc = utcNow,
			PublisherId = "--MessageBus--",
			TraceInfo = traceInfo,
			Timeout = options.Timeout,
			RetryCount = 0,
			ErrorHandling = options.ErrorHandling,
			IdSession = options.IdSession,
			ContentType = options.ContentType,
			ContentEncoding = options.ContentEncoding,
			IsCompressedContent = options.IsCompressContent,
			IsEncryptedContent = options.IsEncryptContent,
			ContainsContent = requestMessage != null,
			Priority = options.Priority,
			Headers = options.Headers?.GetAll(),
			DisabledMessagePersistence = options.DisabledMessagePersistence,
			MessageStatus = MessageStatus.Created,
			DelayedToUtc = null
		};

		if (MessageBusOptions.MessageBodyProvider != null
			&& MessageBusOptions.MessageBodyProvider.AllowMessagePersistence(options.DisabledMessagePersistence, metadata))
		{
			var saveResult = MessageBusOptions.MessageBodyProvider.SaveToStorage(new List<IMessageMetadata> { metadata }, requestMessage, traceInfo, options.TransactionController);
			if (result.MergeHasError(saveResult))
				return result.Build();
		}

		return result.WithData(metadata).Build();
	}

	protected virtual IResult<Guid> SaveResponseMessage<TResponse>(
		TResponse responseMessage,
		IMessageHandlerContext handlerContext,
		ITraceInfo traceInfo)
	{
		traceInfo = TraceInfo.Create(traceInfo);
		var result = new ResultBuilder<Guid>();

		if (MessageBusOptions.MessageBodyProvider != null)
		{
			var saveResult = MessageBusOptions.MessageBodyProvider.SaveReplyToStorage(handlerContext.MessageId, responseMessage, traceInfo, handlerContext.TransactionController);
			if (result.MergeHasError(saveResult))
				return result.Build();

			result.WithData(saveResult.Data);
		}

		return result.Build();
	}
}
