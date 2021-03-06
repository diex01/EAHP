namespace Microsoft.Azure.EventHubs.CaptureProcessor
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Avro;
    using Avro.File;
    using Avro.Generic;
    using Azure.Storage;
    using Microsoft.Azure.EventHubs;
    using Microsoft.Azure.EventHubs.Processor;
    using Microsoft.Azure.Storage.Blob;

    /*
     * TODO
     * EventProcessorOptions - specifically max batch size
     * More flexible captureFileNameFormat - this now only supports default
     * Saving which files you have already read or which you should start with
     */
    public class CaptureProcessorHost
    {
        private readonly EventHubsDetails details;
        private readonly EventProcessorHost host;

        public CaptureProcessorHost(
            string namespaceName, string eventHubName, string eventHubConnectionString, int partitionCount,
            string consumerGroup, string leaseContainerName, string captureStorageAccountConnectionString,
            string captureContainerName, string captureFileNameFormat,
            DateTime? startingAt = null)
        {
            this.details = new EventHubsDetails
            {
                NamespaceName = namespaceName,
                EventHubName = eventHubName,
                PartitionCount = partitionCount,
                CaptureStorageAccountConnectionString = captureStorageAccountConnectionString,
                CaptureContainerName = captureContainerName,
                CaptureFileNameFormat = captureFileNameFormat,
                StartingAt = startingAt,
                ConsumerGroup = consumerGroup,
                LeaseContainerName = leaseContainerName,
                EventHubConnectionString = eventHubConnectionString
            };
            this.host = new EventProcessorHost(
                eventHubPath: details.EventHubName,
                consumerGroupName: details.ConsumerGroup,
                eventHubConnectionString: details.EventHubConnectionString,
                storageConnectionString: details.CaptureStorageAccountConnectionString,
                leaseContainerName: details.LeaseContainerName);
        }

        public Task RunCaptureProcessorAsync(Func<IEventProcessor> newEventProcessor, CancellationToken token = default) =>
            Task.WhenAll(Enumerable
                .Range(0, details.PartitionCount)
                .Select(partitionId => new CaptureProcessor(
                    eventProcessor: newEventProcessor(),
                    eventHubsDetails: this.details,
                    partitionContext: new PartitionContext(
                        host: host,
                        partitionId: partitionId.ToString(),
                        eventHubPath: this.details.EventHubName,
                        consumerGroupName: this.details.ConsumerGroup,
                        cancellationToken: token)))
                .Select(processor => processor.StartPump(token))
                .ToArray());
    }

    internal class EventHubsDetails
    {
        public string NamespaceName { get; internal set; }
        public string EventHubName { get; internal set; }
        public string ConsumerGroup { get; internal set; }
        public string EventHubConnectionString { get; internal set; }
        public int PartitionCount { get; internal set; }
        public string CaptureStorageAccountConnectionString { get; internal set; }
        public string CaptureContainerName { get; internal set; }
        public string LeaseContainerName { get; internal set; }
        public string CaptureFileNameFormat { get; internal set; }
        public DateTime? StartingAt { get; internal set; }
    }

    internal class CaptureProcessor
    {
        private readonly IEventProcessor eventProcessor;
        private readonly PartitionContext partitionContext;
        private readonly EventHubsDetails eventHubsDetails;
        private readonly bool useStartFile = false;
        private readonly string startString;

        internal CaptureProcessor(IEventProcessor eventProcessor, EventHubsDetails eventHubsDetails, PartitionContext partitionContext)
        {
            this.eventProcessor = eventProcessor;
            this.eventHubsDetails = eventHubsDetails;
            this.partitionContext = partitionContext;

            if (eventHubsDetails.StartingAt != null && eventHubsDetails.StartingAt != DateTime.MinValue)
            {
                useStartFile = true;
                startString = GetStartString(partitionContext);
            }
        }

        private string FormatStorageString(PartitionContext partitionContext) =>
            eventHubsDetails.CaptureFileNameFormat
                .Replace("{Namespace}", eventHubsDetails.NamespaceName)
                .Replace("{EventHub}", eventHubsDetails.EventHubName)
                .Replace("{PartitionId}", partitionContext.PartitionId)
                .Replace("{Year}/{Month}/{Day}/{Hour}/{Minute}/{Second}", "");

        private string GetStartString(PartitionContext partitionContext) =>
            eventHubsDetails.CaptureFileNameFormat
                .Replace("{Namespace}", eventHubsDetails.NamespaceName)
                .Replace("{EventHub}", eventHubsDetails.EventHubName)
                .Replace("{PartitionId}", partitionContext.PartitionId)
                .Replace("{Year}", eventHubsDetails.StartingAt.Value.Year.ToString())
                .Replace("{Month}", eventHubsDetails.StartingAt.Value.Month.ToString("D2"))
                .Replace("{Day}", eventHubsDetails.StartingAt.Value.Day.ToString("D2"))
                .Replace("{Hour}", eventHubsDetails.StartingAt.Value.Hour.ToString("D2"))
                .Replace("{Minute}", eventHubsDetails.StartingAt.Value.Minute.ToString("D2"))
                .Replace("{Second}", eventHubsDetails.StartingAt.Value.Second.ToString("D2"));

        public async Task StartPump(CancellationToken cancellationToken = default)
        {
            if (!CloudStorageAccount.TryParse(
                eventHubsDetails.CaptureStorageAccountConnectionString,
                out CloudStorageAccount storageAccount))
            {
                return;
            }

            var cloudBlobClient = storageAccount.CreateCloudBlobClient();
            var captureContainer = cloudBlobClient.GetContainerReference(eventHubsDetails.CaptureContainerName);
            var captureContainerUri = captureContainer.Uri.ToString() + "/";
            string uriPrefix = captureContainerUri + startString;
            string listBlobPrefix = FormatStorageString(partitionContext);
            OperationContext operationContext = null;

            BlobContinuationToken blobContinuationToken = null;
            do
            {
                var blobResultSegment = await captureContainer.ListBlobsSegmentedAsync(
                    prefix: listBlobPrefix,  useFlatBlobListing: true, 
                    blobListingDetails: BlobListingDetails.None, 
                    maxResults: null, currentToken: blobContinuationToken,
                    options: new BlobRequestOptions(), operationContext: operationContext, 
                    cancellationToken: cancellationToken);
                blobContinuationToken = blobResultSegment.ContinuationToken;

                foreach (var listBlobItem in blobResultSegment.Results)
                {
                    if (useStartFile && string.Compare(uriPrefix, listBlobItem.Uri.AbsoluteUri) > 0)
                    {
                        continue;
                    }

                    var blob = await cloudBlobClient.GetBlobReferenceFromServerAsync(
                        blobUri: listBlobItem.Uri, cancellationToken: cancellationToken);

                    using Stream stream = await blob.OpenReadAsync(
                        accessCondition: null, 
                        options: new BlobRequestOptions(), 
                        operationContext: operationContext, 
                        cancellationToken: cancellationToken);

                    IEnumerable<EventData> eventHubMessages = stream.ReadAvroStreamToEventHubData(
                        partitionKey: this.partitionContext.PartitionId);

                    await eventProcessor.ProcessEventsAsync(
                        context: partitionContext,
                        messages: eventHubMessages);
                }
            } while (blobContinuationToken != null);
        }
    }

    internal static class CaptureProcessorHostExtensions
    {
        internal static async Task ForeachAwaiting<T>(this IEnumerable<T> values, Func<T, Task> action)
        {
            foreach (var value in values)
            {
                await action(value);
            }
        }

        internal static void Foreach<T>(this IEnumerable<T> values, Action<T> action)
        {
            foreach (var value in values)
            {
                action(value);
            }
        }

        private static T GetValue<T>(this GenericRecord record, string fieldName)
        {
            if (!record.TryGetValue(fieldName, out object result))
            {
                throw new ArgumentException($"Missing field {fieldName} in {nameof(GenericRecord)} object.");
            }
            return (T)result;
        }

        private static DateTime ParseTime(this string s) => DateTime.ParseExact(s, format: "M/d/yyyy h:mm:ss tt",
                    provider: CultureInfo.InvariantCulture, style: DateTimeStyles.AssumeUniversal);

        internal static IEnumerable<EventData> ReadAvroStreamToEventHubData(this Stream stream, string partitionKey)
        {
            using var reader = DataFileReader<GenericRecord>.OpenReader(stream);
            while (reader.HasNext())
            {
                GenericRecord genericAvroRecord = reader.Next();

                var body = genericAvroRecord.GetValue<byte[]>(nameof(EventData.Body));
                var sequenceNumber = genericAvroRecord.GetValue<long>(nameof(EventData.SystemProperties.SequenceNumber));
                var enqueuedTimeUtc = genericAvroRecord.GetValue<string>(nameof(EventData.SystemProperties.EnqueuedTimeUtc)).ParseTime();
                var offset = genericAvroRecord.GetValue<string>(nameof(EventData.SystemProperties.Offset));

                var systemPropertiesCollection = new EventData.SystemPropertiesCollection(
                        sequenceNumber: sequenceNumber, enqueuedTimeUtc: enqueuedTimeUtc,
                        offset: offset, partitionKey: partitionKey);
                genericAvroRecord
                    .GetValue<Dictionary<string, object>>(nameof(EventData.SystemProperties))
                    .Foreach(x => systemPropertiesCollection.Add(x.Key, x.Value));

                IEnumerator<Field> avroSchemaField = genericAvroRecord.Schema.GetEnumerator();
                while (avroSchemaField.MoveNext())
                {
                    var currentAvroSchemaField = avroSchemaField.Current;
                    var currentFieldName = currentAvroSchemaField.Name;

                    if (currentFieldName == nameof(EventData.Body)) continue;
                    if (currentFieldName == nameof(EventData.Properties)) continue;
                    if (currentFieldName == nameof(EventData.SystemProperties)) continue;

                    if (genericAvroRecord.TryGetValue(currentFieldName, out object prop))
                    {
                        systemPropertiesCollection[currentFieldName] = prop;
                    }
                }

                var eventData = new EventData(body)
                {
                    SystemProperties = systemPropertiesCollection
                };

                genericAvroRecord
                    .GetValue<Dictionary<string, object>>(nameof(EventData.Properties))
                    .Foreach(eventData.Properties.Add);

                yield return eventData;
            }
        }
    }
}