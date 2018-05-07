﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeviceGroupsAgent.Test.Helpers;
using Microsoft.Azure.EventHubs.Processor;
using Microsoft.Azure.IoTSolutions.AsaManager.DeviceGroupsAgent;
using Microsoft.Azure.IoTSolutions.AsaManager.DeviceGroupsAgent.Models;
using Microsoft.Azure.IoTSolutions.AsaManager.Services.Concurrency;
using Microsoft.Azure.IoTSolutions.AsaManager.Services.Diagnostics;
using Microsoft.Azure.IoTSolutions.AsaManager.Services.EventHub;
using Microsoft.Azure.IoTSolutions.AsaManager.Services.Runtime;
using Moq;
using Xunit;

namespace DeviceGroupsAgent.Test
{
    public class AgentTest
    {
        private readonly Mock<ILogger> logMock;
        private readonly Mock<IDeviceGroupsClient> deviceGroupsClientMock;
        private readonly Mock<IEventProcessorHostWrapper> eventProcessorHostWrapperMock;
        private readonly Mock<IDeviceGroupsWriter> deviceGroupsWriterMock;
        private readonly Mock<IThreadWrapper> threadMock;
        private readonly Mock<IEventHubStatus> eventHubStatusMock;

        private readonly IEventProcessorFactory eventProcessorFactory;
        private readonly IAgent deviceGroupsAgent;
        private DeviceGroupListApiModel deviceGroupListApiModel;

        private CancellationTokenSource agentsRunState;
        private CancellationToken runState;

        private const int TEST_TIMEOUT_MS = 10000;

        public AgentTest()
        {
            var blobStorageConfigMock = new Mock<IBlobStorageConfig>();

            this.logMock = new Mock<ILogger>();

            this.deviceGroupsWriterMock = new Mock<IDeviceGroupsWriter>();
            this.deviceGroupsClientMock = new Mock<IDeviceGroupsClient>();
            this.eventProcessorHostWrapperMock = new Mock<IEventProcessorHostWrapper>();
            var servicesConfigMock = new Mock<IServicesConfig>();
            servicesConfigMock.Setup(x => x.EventHubCheckpointTimeMs).Returns(60000);

            this.eventHubStatusMock = new Mock<IEventHubStatus>();
            this.eventProcessorFactory = new DeviceEventProcessorFactory(this.eventHubStatusMock.Object, servicesConfigMock.Object, this.logMock.Object);

            this.threadMock = new Mock<IThreadWrapper>();

            this.deviceGroupsAgent = new Agent(
                this.deviceGroupsWriterMock.Object,
                this.deviceGroupsClientMock.Object,
                this.eventProcessorHostWrapperMock.Object,
                this.eventProcessorFactory,
                this.eventHubStatusMock.Object,
                servicesConfigMock.Object,
                blobStorageConfigMock.Object,
                this.logMock.Object,
                this.threadMock.Object);
        }

        /**
         * Test basic end to end functionality of DeviceGroupsAgent.
         * Verify data will be written on start, but not again if there are not changes seen
         */
        [Fact, Trait(Constants.TYPE, Constants.UNIT_TEST)]
        public void BasicEndToEndTest()
        {
            // Arrange
            this.StopAgentAfterNLoops(5);
            this.SetupDeviceGroupsClientMock();
            this.SetupEventHubStatusMockAlwaysFalse();

            // Act
            this.deviceGroupsAgent.RunAsync(this.runState).Wait(TEST_TIMEOUT_MS);

            // Assert
            this.VerifyEndToEndResults(5, 1);
            this.deviceGroupsClientMock.Verify(d => d.GetGroupToDevicesMappingAsync(
                this.deviceGroupListApiModel),
                Times.Exactly(1));
        }

        /**
         * Test basic device group definition functionality of DeviceGroupsAgent.
         * Verify data will be written when device group definitions change
         */
        [Fact, Trait(Constants.TYPE, Constants.UNIT_TEST)]
        public void VerifyNewDataWrittenWhenDeviceGroupChanges()
        {
            // Arrange
            this.StopAgentAfterNLoops(3);
            this.SetupSequenceForDeviceGroupsClientMock();
            this.SetupEventHubStatusMockAlwaysFalse();

            // Act
            this.deviceGroupsAgent.RunAsync(this.runState).Wait(TEST_TIMEOUT_MS);

            // Assert
            this.VerifyEndToEndResults(3, 3);
            this.deviceGroupsClientMock.Verify(d => d.GetGroupToDevicesMappingAsync(
                It.IsAny<DeviceGroupListApiModel>()),
                Times.Exactly(3));
        }

        /**
         * Test basic event hub functionality of DeviceGroupsAgent.
         * Verify data will be written on start, and when EventHubStatus.HasSeenChanges returns true,
         * and the "minute" after event hub has seen changes
         */
        [Fact, Trait(Constants.TYPE, Constants.UNIT_TEST)]
        public void VerifyNewDataWrittenWhenEventHubStatusChanges()
        {
            // Arrange
            this.StopAgentAfterNLoops(4);
            this.SetupDeviceGroupsClientMock();
            this.SetupSequenceForEventHubStatusMock();

            // Act
            this.deviceGroupsAgent.RunAsync(this.runState).Wait(TEST_TIMEOUT_MS);

            // Assert
            this.VerifyEndToEndResults(4, 3);
            this.deviceGroupsClientMock.Verify(d => d.GetGroupToDevicesMappingAsync(
                this.deviceGroupListApiModel),
                Times.Exactly(3));
        }

        // Set up device group client to return dummy device group list that will not change
        private void SetupDeviceGroupsClientMock()
        {
            this.deviceGroupListApiModel = TestHelperFunctions.CreateDeviceGroupListApiModel("etag1", "groupid");
            this.deviceGroupsClientMock.Setup(x => x.GetDeviceGroupsAsync()).Returns(Task.FromResult(this.deviceGroupListApiModel));
        }

        /**
         * Sets up sequence for DeviceGroupsClient.GetDeviceGroupsAsync
         * that will return groups with different etags
         */
        private void SetupSequenceForDeviceGroupsClientMock()
        {
            this.deviceGroupsClientMock.SetupSequence(x => x.GetDeviceGroupsAsync())
                .Returns(Task.FromResult(TestHelperFunctions.CreateDeviceGroupListApiModel("etag1", "group1")))
                .Returns(Task.FromResult(TestHelperFunctions.CreateDeviceGroupListApiModel("etag2", "group1")))
                .Returns(Task.FromResult(TestHelperFunctions.CreateDeviceGroupListApiModel("etag3", "group1")));
        }

        // Sets up EventHubStatus.HasSeenChanges to always return false
        private void SetupEventHubStatusMockAlwaysFalse()
        {
            this.eventHubStatusMock.Setup(x => x.SeenChanges).Returns(false);
        }

        /**
         * Sets up sequence for EventHubStatus.HasSeenChanges
         * that will alternate between false and true, per iteration
         * (EventHubStatus.HasSeenChanges is called multiple times per iteration,
         * see comments for line numbers
         */
        private void SetupSequenceForEventHubStatusMock()
        {
            this.eventHubStatusMock.SetupSequence(x => x.SeenChanges)
                // Agent.cs:73
                .Returns(false)
                // Agent.cs:94
                .Returns(false)
                // Agent.cs:96
                .Returns(false)
                // Agent.cs:94
                .Returns(true)
                // Agent.cs:96
                .Returns(true)
                // Agent.cs:94
                .Returns(false)
                // Agent.cs:96
                .Returns(false)
                // Agent.cs:94
                .Returns(false)
                // Agent.cs:96
                .Returns(false);
        }

        private void StopAgentAfterNLoops(int n)
        {
            // A new cancellation token is required every time
            this.agentsRunState = new CancellationTokenSource();
            this.runState = this.agentsRunState.Token;

            this.threadMock
                .Setup(x => x.Sleep(It.IsAny<int>()))
                .Callback(() =>
                {
                    if (--n <= 0) this.agentsRunState.Cancel();
                });
        }

        /**
         * Verifies agent functionality, where agent ran "loops" times
         * and is expected to write to blob storage "expectedWrites" times
         * Verifies event hub processor is set up, and expected loops and
         * writes occur.
         */
        private void VerifyEndToEndResults(int loops, int expectedWrites)
        {
            TestHelperFunctions.VerifyErrorsLogged(this.logMock, 0);

            this.eventProcessorHostWrapperMock.Verify(e => e.CreateEventProcessorHost(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()),
                Times.Once());
            this.eventProcessorHostWrapperMock.Verify(e => e.RegisterEventProcessorFactoryAsync(
                    It.IsAny<EventProcessorHost>(),
                    this.eventProcessorFactory),
                Times.Once());

            this.deviceGroupsClientMock.Verify(d => d.GetDeviceGroupsAsync(), Times.Exactly(loops));

            this.deviceGroupsWriterMock.Verify(d => d.ExportMapToReferenceDataAsync(
                    It.IsAny<Dictionary<string, IEnumerable<string>>>(),
                    It.IsAny<DateTimeOffset>()),
                Times.Exactly(expectedWrites));
        }
    }
}
