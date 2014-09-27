﻿using SharpNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Criteo
{
    public class CriteoTrainer : SGDTrainer
    {
        public CriteoTrainer(Network net, OneHotRecordProvider trainDataProvider, OneHotRecordProvider testDataProvider)
            : base(net, trainDataProvider, testDataProvider)
        {
        }

        public OneHotRecordProvider TrainProvider
        {
            get { return (OneHotRecordProvider)_trainDataProvider; }
        }

        public OneHotRecordProvider TestProvider
        {
            get { return (OneHotRecordProvider)_testDataProvider; }
        }

        bool pretest = true;
        public void Train(float learnRate, float momentum, int epocsBeforeReport, int epocsBeforeMergeHoldout, int totalEpochs)
        {
            _network.TrainDataProvider = _trainDataProvider;
            _network.TestDataProvider = _testDataProvider;
            _network.SamplesPerMovingAverage = 40;
            _network.CopyToGpu();
            var epochNo = 0;
            var batchNo = 0;
            while (epochNo < totalEpochs)
            {
                if (TrainProvider.CurrentSet % epocsBeforeReport == 0 && TrainProvider.CurrentSet != 0 && TrainProvider._currentBatch == 1)
                {
                    ComputeLogLoss(epochNo, batchNo, test: false);
                    ComputeLogLoss(epochNo, batchNo, test: true);
                    Console.WriteLine(_network.GetTrainRecordsPerSecond() + " records per second");
                }

                if ((epochNo == epocsBeforeMergeHoldout)&& (pretest))
                {
                    Console.WriteLine("Merging holdout set to trainset");
                    pretest = false;
                    TrainProvider._records.AddRange(TrainProvider._records);
                    learnRate /= 2f; // Lower learnrate a bit
                }
                
                _network.Calculate(train: true);
                _network.BackPropagate();
                _network.ApplyWeightUpdates(learnRate, momentum);


                batchNo = TrainProvider.CurrentSet;
                epochNo = TrainProvider._currentEpoch;

            }
        }

        public void ComputeLogLoss(int epochNo, int batchNo, bool test = true)
        {
            var provider = (test) ? TestProvider : TrainProvider;
            provider.LoadNextBatchSet();
            var setNo = provider.CurrentSet;
            var correctPercentage = 0f;
            var recordCount = 0;
            var sum = 0d;

            DataBatch firstTestBatch = null;
            while (setNo == provider.CurrentSet)
            {
                _network.Calculate(train: !test);
                var testBatch = provider.CurrentBatch;
                if (firstTestBatch == null) firstTestBatch = testBatch;
                testBatch.PredictedLabels = _network.CostLayer.GetPredictedLabels();
                if (testBatch == provider.Batches.Last())
                {
                    correctPercentage = provider.GetCorrectLabelPercentage();
                }

                _network.CostLayer.Outputs.CopyToHost();
                var eps = 1e-15;
                for (int i = 0; i < Constants.MINIBATCH_SIZE; i++)
                {
                    var real = (double)testBatch.Labels[i];
                    var pred = (double)_network.CostLayer.Outputs[i, 1];
                    if (pred<eps) pred = eps;
                    if (pred > (1d-eps)) pred = 1d - eps;
                    var l = real * Math.Log(pred) + (1d - real) * Math.Log(1d - pred);
                    sum += -l;
                    recordCount++;
                }
            }
            var logLoss = sum / (double)recordCount;


            var fNo = (firstTestBatch.EpocNo + "." + firstTestBatch.BatchNo).PadRight(10);
            var txt = (test) ? "Holdout" : "Train";
            if (logLoss < 0.44f) Console.ForegroundColor = ConsoleColor.Green;
            if (logLoss > 0.44f) Console.ForegroundColor = ConsoleColor.Yellow;
            if (logLoss > 0.45f) Console.ForegroundColor = ConsoleColor.White;
            if (logLoss > 0.47f) Console.ForegroundColor = ConsoleColor.Red;
            if (logLoss > 0.49f) Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine(txt + " " + fNo + " correct : " + correctPercentage + " logloss = " + logLoss);
            if (logLoss > 0.45f) Console.ForegroundColor = ConsoleColor.White;
        }
    }
}
