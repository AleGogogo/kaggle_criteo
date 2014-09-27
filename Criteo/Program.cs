﻿using SharpNet;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Criteo
{
    class Program
    {
        static void Main(string[] args)
        {
            var dataDir = Directory.GetCurrentDirectory();
            var csvTrainPath = Path.Combine(dataDir, "train.csv");
            var csvTestPath = Path.Combine(dataDir, "test.csv");
            var binTrainPath = Path.Combine(dataDir, "train_bin.bin");
            var binTestPath = Path.Combine(dataDir, "test_bin.bin");
            var recodedTrainPath = Path.Combine(dataDir, "train_recoded.bin");
            var recodedTestPath = Path.Combine(dataDir, "test_recoded.bin");
            var oneHotTrainPath = Path.Combine(dataDir, "train_onehot.bin");
            var oneHotTestPath = Path.Combine(dataDir, "test_onehot.bin");
            var scaledTrainPath = Path.Combine(dataDir, "train_scaled.bin");
            var scaledTestPath = Path.Combine(dataDir, "test_scaled.bin");

            Constants.HASH_SPACE_SIZE = 32768 * 2; // Like 'b' in vowpal but much smaller. We have less space on GPU and we need to multiply the space with the amount of nodes in the 1st layer
                                                   // When you change the value you need to preprocess again..
            Constants.InitOneHotIndices();

            var preprocess = true; // You don't need to preprocess every run.. also when preprocessing, you don't need to do every step every time..
            if (preprocess)
            {

                // First process the CSV data into "zipped binary data" useful for when we have to reprocess. Faster and more compact..
                PreprocessingRawValues.ConvertCSVToBinary(csvTrainPath, binTrainPath);
                PreprocessingRawValues.ConvertCSVToBinary(csvTestPath, binTestPath);

                // Recode categorical values. MISSING = missing, TRAINNOTTEST = in trainset, not testset, TESTNOTTRAIN = in testset, not trainset
                // LOWFREQUENCY = When a value occurs below a certain threshold, it is recoded to this value.
                var frequencyFilter = Constants.FREQUENCY_FILTER_AGGRESSIVE; // Vary for ensembling, Medium or mild results in more featurevalues = more GPU mem usage, potentially better accuracy but also potentially overfitting. Make sure you also increase HASH_SIZE
                PreprocessingRawValues.RecodeCategoricalValues(binTrainPath, binTestPath, recodedTrainPath, recodedTestPath, frequencyFilter);

                // Now One-Hot encode the raw records. (actually it one-hot encodes the categories with few values and hashes the categories with many values)
                // This is probably way too complicated. Perhaps we could hash everything. Even the numeric values.
                var encodeMissingValues = true;  // vary for ensembling
                var logTransformNumerics = true; // vary for ensembling
                var encodeTestNotTrainAs = Constants.VALUE_MISSING; // vary for ensembling
                PreprocessingRawToOneHot.ConvertRawToOneHot(recodedTrainPath, recodedTestPath, oneHotTrainPath, oneHotTestPath, encodeMissingValues, encodeTestNotTrainAs, logTransformNumerics);

                // Now scale the numeric values. This leads to faster convergence..
                PreprocessingScale.ScaleNumericValues(oneHotTrainPath, oneHotTestPath, scaledTrainPath, scaledTestPath);
            }
            
            // We create an "ensemble" of a relunet and a maxout net.

            var gpuModule = new GPUModule();
            var learnRate = 0.02f; // 0.04 also worked fine for me..
            var momentum = 0.5f; // Did not play with this much since 1st layer is without momentum for performance reasons.
            var epochsBeforeMergeHoldout = 30; // When do we add the holdout set to the trainset (no more validation information)
            var totalEpochs = 50; // How many epochs to train.. Usually I saw no improvement after 50

            var reluNet = CriteoNet.CreateNetworkRelu(gpuModule, Constants.MINIBATCH_SIZE); // Example network that worked fine
            Train(scaledTrainPath, scaledTestPath, reluNet, learnRate, momentum, epochsBeforeMergeHoldout, totalEpochs);
            // Make a net with smaller minibatchsize for submission..
            var paramReluPath = Path.Combine(dataDir, "paramsRelu.xml");
            var submissionReluPath = Path.Combine(dataDir, "submissionRelu.csv");
            reluNet.SaveWeightsAndParams(paramReluPath);
            reluNet = CriteoNet.CreateNetworkRelu(gpuModule, 15);
            reluNet.LoadStructureWeightsAndParams(paramReluPath);
            MakeSubmission(reluNet, scaledTestPath, submissionReluPath, batchSize: 15);

            var maxoutNet = CriteoNet.CreateNetworkMaxout(gpuModule, Constants.MINIBATCH_SIZE); // Example network that worked fine
            Train(scaledTrainPath, scaledTestPath, reluNet, learnRate, momentum, epochsBeforeMergeHoldout, totalEpochs);
            // Make a net with smaller minibatchsize for submission..
            var paramMaxoutPath = Path.Combine(dataDir, "paramsMaxout.xml");
            var submissionMaxoutPath = Path.Combine(dataDir, "submissionMaxout.csv");
            maxoutNet.SaveWeightsAndParams(paramReluPath);
            maxoutNet = CriteoNet.CreateNetworkRelu(gpuModule, 15);
            maxoutNet.LoadStructureWeightsAndParams(paramMaxoutPath);
            MakeSubmission(maxoutNet, scaledTestPath, submissionMaxoutPath, batchSize: 15);

            // Now make the combined submission
            var submissionCombinedPath = Path.Combine(dataDir, "submissionCombined.csv");
            CombineSubmission(submissionCombinedPath, new string[] { submissionReluPath, submissionMaxoutPath });

            Console.WriteLine("Done press enter");
            Console.ReadLine();
        }


        public static void Train(string trainSetPath, string testSetPath, Network net, float learnRate = 0.02f, float momentum = 0.5f, int epochsBeforeMergeHoldout = 30, int totalEpochs = 50)
        {
            var module = new GPUModule();
            module.InitGPU();
            var trainRecords = OneHotRecordReadOnly.LoadBinary(trainSetPath );

            // use roughly last day for validation
            var trainCount = trainRecords.Count;
            var holdoutCount = trainCount / 7;
            trainCount = trainCount - holdoutCount;
            var holdoutRecords = trainRecords.Skip(trainCount).ToList();
            holdoutRecords.Shuffle();
            trainRecords = trainRecords.Take(trainCount).ToList();

            var trainProvider = new OneHotRecordProvider(module, trainRecords, "train", shuffleEveryEpoch: true);
            //var trainProvider = new ClicksProvider(module, TRAINSET_BIN_PATH, "train");
            trainProvider._currentEpoch = 0;
            var holdoutProvider = new OneHotRecordProvider(module, holdoutRecords, "test");// new ClicksProvider(module, TESTSET_BIN_PATH, "test");
            //var testProvider = new ClicksProvider(module, TESTSET_BIN_PATH, "test");
            holdoutProvider._currentEpoch = 0;

            var trainer = new CriteoTrainer(net, trainProvider, holdoutProvider);
            trainer.Train(learnRate, momentum, epocsBeforeReport: 40, epocsBeforeMergeHoldout: epochsBeforeMergeHoldout, totalEpochs: totalEpochs );
        }

        public static void MakeSubmission(Network network, string testsetPath, string targetPath, int batchSize)
        {
            Console.WriteLine("Making submission");

            var recs = OneHotRecordReadOnly.LoadBinary(testsetPath);
            var sparseIndices = new int[batchSize][];
            var sparseValues = new float[batchSize][];
            var labels = new float[batchSize];
            var ids = new int[batchSize];

            var submissionLines = new List<SubmissionLine>();

            for (var recNo = 0; recNo < recs.Count; recNo++)
            {
                var record = recs[recNo];
                var label = record.Label;
                var id = record.Id;

                labels[recNo % batchSize] = label;
                ids[recNo % batchSize] = id;
                record.CopyDataToSparseArray(sparseIndices, sparseValues, recNo % batchSize);

                if (((recNo + 1) % batchSize) == 0)
                {
                    network.InputLayer.SetSparseData(sparseValues.ToList(), sparseIndices.ToList());
                    network.LabelLayer.SetData(labels);
                    network.Calculate(train: false);
                    network.CostLayer.CopyToHost();
                    for (var i = 0; i < batchSize; i++)
                    {
                        var chance = network.CostLayer.Outputs[i, 1];
                        var line = new SubmissionLine();
                        line.Id = ids[i];
                        line.Chance = chance;
                        submissionLines.Add(line);
                    }
                }
                if (recNo % 100000 == 0)
                {
                    Console.WriteLine("line : " + recNo);
                }
            }
            SubmissionLine.SaveSubmission(testsetPath, submissionLines);
        }

        public static void CombineSubmission(string dstPath, params string[] srcFiles)
        {
            var submissionIndex = new Dictionary<int, SubmissionLine>();
            var submissionList = new List<SubmissionLine>();

            for (var i = 0; i < srcFiles.Length; i++)
            {
                var path = srcFiles[i];
                Console.WriteLine("Processing submission : " + Path.GetFileName(path));
                var lineNo = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNo++;
                    if (lineNo == 1) continue;
                    var values = line.Split(',');
                    var id = Int32.Parse(values[0]);
                    var chance = float.Parse(values[1], CultureInfo.InvariantCulture);
                    if (!submissionIndex.ContainsKey(id))
                    {
                        var newLine = new SubmissionLine();
                        newLine.Id = id;
                        newLine.Chance = 0f;
                        submissionIndex[id] = newLine;
                        submissionList.Add(newLine);
                    }

                    var subLine = submissionIndex[id];
                    subLine.Chance += chance;

                    if (lineNo % 1000000 == 0)
                    {
                        Console.WriteLine("line : " + lineNo);
                    }
                }
            }

            foreach (var submission in submissionList)
            {
                submission.Chance = submission.Chance / srcFiles.Length;
            }

            SubmissionLine.SaveSubmission(dstPath, submissionList);
        }

    }
}
