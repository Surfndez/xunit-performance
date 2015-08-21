﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using MathNet.Numerics.Statistics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Microsoft.Xunit.Performance.Analysis
{
    internal class Program
    {
        private const double ErrorConfidence = 0.95; // TODO: make configurable

        private static int Usage()
        {
            Console.Error.WriteLine(
                "usage: xunit.performance.analysis <etlPaths> [-compare \"baselineRunId\" \"comparisonRunId\"]  [-xml <output.xml>] [-html <output.html>]");
            return 1;
        }

        private static int Main(string[] args)
        {
            var etlPaths = new List<string>();
            var allComparisonIds = new List<Tuple<string, string>>();
            var xmlOutputPath = (string)null;
            var htmlOutputPath = (string)null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("-") || args[i].StartsWith("/"))
                {
                    string switchName = args[i].Substring(1).ToLowerInvariant();
                    switch (switchName)
                    {
                        case "compare":
                            if (++i >= args.Length)
                                return Usage();
                            string baseline = args[i];
                            if (++i >= args.Length)
                                return Usage();
                            string comparison = args[i];
                            allComparisonIds.Add(Tuple.Create(baseline, comparison));
                            break;

                        case "xml":
                            if (++i >= args.Length)
                                return Usage();
                            xmlOutputPath = args[i];
                            break;

                        case "html":
                            if (++i >= args.Length)
                                return Usage();
                            htmlOutputPath = args[i];
                            break;

                        default:
                            return Usage();
                    }
                }
                else
                {
                    bool foundFile = false;
                    foreach (var file in ExpandFilePath(args[i]))
                    {
                        if (file.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".etl.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            foundFile = true;
                            etlPaths.Add(file);
                        }
                        else
                        {
                            Console.Error.WriteLine($"{file}' is not a .etl or .etl.zip file.");
                            return 1;
                        }
                    }
                    if (!foundFile)
                    {
                        Console.Error.WriteLine($"The path '{args[i]}' could not be found.");
                        return 1;
                    }
                }
            }

            if (etlPaths.Count == 0)
                return Usage();

            var allIterations = ParseEtlFiles(etlPaths);

            var testResults = SummarizeTestResults(allIterations);

            var comparisonResults = DoComparisons(allComparisonIds, testResults);

            if (xmlOutputPath != null)
                WriteTestResultsXml(testResults, comparisonResults).Save(xmlOutputPath);

            if (htmlOutputPath != null)
                WriteTestResultsHtml(testResults, comparisonResults, htmlOutputPath);

            return 0;
        }

        private static List<TestResultComparison> DoComparisons(List<Tuple<string, string>> allComparisonIds, Dictionary<string, Dictionary<string, TestResult>> testResults)
        {
            var comparisonResults = new List<TestResultComparison>();

            foreach (var comparisonIds in allComparisonIds)
            {
                var baseline = testResults[comparisonIds.Item1];
                var comparison = testResults[comparisonIds.Item2];

                foreach (var comparisonTest in comparison.Values)
                {
                    var baselineTest = baseline[comparisonTest.TestName];

                    // Compute the standard error in the difference
                    var baselineCount = baselineTest.Iterations.Count;
                    var baselineSum = baselineTest.Iterations.Sum(iteration => iteration.Duration);
                    var baselineSumSquared = baselineSum * baselineSum;
                    var baselineSumOfSquares = baselineTest.Iterations.Sum(iteration => iteration.Duration * iteration.Duration);

                    var comparisonCount = comparisonTest.Iterations.Count;
                    var comparisonSum = comparisonTest.Iterations.Sum(iteration => iteration.Duration);
                    var comparisonSumSquared = comparisonSum * comparisonSum;
                    var comparisonSumOfSquares = comparisonTest.Iterations.Sum(iteration => iteration.Duration * iteration.Duration);

                    var stdErrorDiff = Math.Sqrt((baselineSumOfSquares - (baselineSumSquared / baselineCount) + comparisonSumOfSquares - (comparisonSumSquared / comparisonCount)) * (1.0 / baselineCount + 1.0 / comparisonCount) / (baselineCount + comparisonCount - 1));
                    var interval = stdErrorDiff * MathNet.Numerics.ExcelFunctions.TInv(1.0 - ErrorConfidence, baselineCount + comparisonCount - 2);

                    var comparisonResult = new TestResultComparison();
                    comparisonResult.BaselineResult = baselineTest;
                    comparisonResult.ComparisonResult = comparisonTest;
                    comparisonResult.TestName = comparisonTest.TestName;
                    comparisonResult.PercentChange = (comparisonTest.DurationStats.Mean - baselineTest.DurationStats.Mean) / baselineTest.DurationStats.Mean;
                    comparisonResult.PercentChangeError = interval / baselineTest.DurationStats.Mean;

                    comparisonResults.Add(comparisonResult);
                }
            }

            return comparisonResults;
        }

        private static Dictionary<string, Dictionary<string, TestResult>> SummarizeTestResults(IEnumerable<TestIterationResult> allIterations)
        {
            var testResults = new Dictionary<string, Dictionary<string, TestResult>>();

            foreach (var iteration in allIterations)
            {
                Dictionary<string, TestResult> runResults;
                if (!testResults.TryGetValue(iteration.RunId, out runResults))
                    testResults[iteration.RunId] = runResults = new Dictionary<string, TestResult>();

                TestResult result;
                if (!runResults.TryGetValue(iteration.TestName, out result))
                {
                    runResults[iteration.TestName] = result = new TestResult();
                    result.RunId = iteration.RunId;
                    result.TestName = iteration.TestName;
                }

                result.DurationStats.Push(iteration.Duration);
                result.GCCountStats.Push(iteration.GCCount);
                result.Failed |= iteration.Failed;

                result.Iterations.Add(iteration);
            }

            return testResults;
        }

        private static XDocument WriteTestResultsXml(Dictionary<string, Dictionary<string, TestResult>> testResults, List<TestResultComparison> comparisonResults)
        {
            var resultElem = new XElement("results");
            var xmlDoc = new XDocument(resultElem);

            foreach (var run in testResults)
            {
                var runIdElem = new XElement("run", new XAttribute("id", run.Key));
                resultElem.Add(runIdElem);

                foreach (var result in run.Value.Values)
                {
                    var testElem = new XElement("test", new XAttribute("name", result.TestName));
                    runIdElem.Add(testElem);

                    if (result.Failed)
                    {
                        testElem.Add(new XAttribute("failed", true));
                    }
                    else
                    {
                        testElem.Add(
                            new XElement("summary",
                                new XElement("duration",
                                    new XAttribute("unit", "milliseconds"),
                                    new XAttribute("min", result.DurationStats.Minimum.ToString("G3")),
                                    new XAttribute("mean", result.DurationStats.Mean.ToString("G3")),
                                    new XAttribute("max", result.DurationStats.Maximum.ToString("G3")),
                                    new XAttribute("marginOfError", result.DurationStats.MarginOfError(ErrorConfidence).ToString("G3")),
                                    new XAttribute("stddev", result.DurationStats.StandardDeviation.ToString("G3"))
                                ),
                                new XElement("gcCount",
                                    new XAttribute("unit", "count"),
                                    new XAttribute("min", result.GCCountStats.Minimum.ToString("G3")),
                                    new XAttribute("mean", result.GCCountStats.Mean.ToString("G3")),
                                    new XAttribute("max", result.GCCountStats.Maximum.ToString("G3")),
                                    new XAttribute("marginOfError", result.GCCountStats.MarginOfError(ErrorConfidence).ToString("G3")),
                                    new XAttribute("stddev", result.GCCountStats.StandardDeviation.ToString("G3"))
                                )
                            )
                        );
                    }
                }
            }

            foreach (var comparison in comparisonResults)
            {
                var comparisonElem = new XElement("comparison", new XAttribute("test", comparison.TestName), new XAttribute("baselineId", comparison.BaselineResult.RunId), new XAttribute("comparisonId", comparison.ComparisonResult.RunId));
                resultElem.Add(comparisonElem);

                comparisonElem.Add(
                    new XElement("duration",
                        new XAttribute("changeRatio", comparison.PercentChange.ToString("G3")),
                        new XAttribute("changeRatioError", comparison.PercentChangeError.ToString("G3"))));
            }

            return xmlDoc;
        }

        private static void WriteTestResultsHtml(Dictionary<string, Dictionary<string, TestResult>> testResults, List<TestResultComparison> comparisonResults, string htmlOutputPath)
        {
            using (var writer = new StreamWriter(htmlOutputPath, false, Encoding.UTF8))
            {
                writer.WriteLine("<html><body>");

                foreach (var comparison in comparisonResults.GroupBy(r => $"Comparison: {r.ComparisonResult.RunId} | Baseline: {r.BaselineResult.RunId}"))
                {
                    writer.WriteLine($"<h1>{comparison.Key}</h1>");
                    writer.WriteLine("<table>");
                    foreach (var test in from c in comparison orderby c.SortChange descending select c)
                    {
                        var passed = test.Passed;
                        string color;
                        if (!passed.HasValue)
                            color = "black";
                        else if (passed.Value)
                            color = "green";
                        else
                            color = "red";
                        writer.WriteLine($"<tr><td>{test.TestName}</td><td><font  color={color}>{test.PercentChange.ToString("+##.#%;-##.#%")}</font></td><td>+/-{test.PercentChangeError.ToString("P1")}</td></tr>");
                    }
                    writer.WriteLine("</table>");
                }

                writer.WriteLine("<hr>");

                foreach (var run in testResults)
                {
                    writer.WriteLine($"<h1>Indivdual results: {run.Key}</h1>");

                    writer.WriteLine($"<table>");
                    writer.WriteLine($"<tr><th>Test</th><th>Unit</th><th>Min</th><th>Mean</th><th>Max</th><th>Margin</th><th>StdDev</th></tr>");
                    foreach (var test in run.Value)
                    {
                        writer.WriteLine($"<tr><td>{test.Value.TestName}</td><td>ms</td><td>{test.Value.DurationStats.Minimum.ToString("G3")}</td><td>{test.Value.DurationStats.Mean.ToString("G3")}</td><td>{test.Value.DurationStats.Maximum.ToString("G3")}</td><td>{test.Value.DurationStats.MarginOfError(ErrorConfidence).ToString("P1")}</td><td>{test.Value.DurationStats.StandardDeviation.ToString("G3")}</td></tr>");
                    }
                    writer.WriteLine($"</table>");
                }

                writer.WriteLine("</html></body>");
            }
        }


        private static IEnumerable<string> ExpandFilePath(string path)
        {
            if (File.Exists(path))
            {
                yield return path;
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*.etl"))
                    yield return file;

                foreach (var file in Directory.EnumerateFiles(path, "*.etl.zip"))
                    yield return file;
            }
        }

        private class TestResult
        {
            public string TestName;
            public string RunId;
            public bool Failed;
            public RunningStatistics DurationStats = new RunningStatistics();
            public RunningStatistics GCCountStats = new RunningStatistics();
            public List<TestIterationResult> Iterations = new List<TestIterationResult>();
        }

        private class TestIterationResult
        {
            public string EtlPath;
            public string RunId;
            public string TestName;
            public int TestIteration;
            public DateTime StartTime;
            public double RelativeStartMilliseconds;
            public double RelativeStopMilliseconds;
            public double Duration => RelativeStopMilliseconds - RelativeStartMilliseconds;
            public int GCCount;
            public bool Failed;

            public HashSet<int> tempProcessIds = new HashSet<int>(); // process IDs active for this iteration; used only while parsing.
        }

        private class TestResultComparison
        {
            public string TestName;
            public TestResult BaselineResult;
            public TestResult ComparisonResult;
            public double PercentChange;
            public double PercentChangeError;
            public double SortChange => (PercentChange > 0) ? Math.Max(PercentChange - PercentChangeError, 0) : Math.Min(PercentChange + PercentChangeError, 0);
            public bool? Passed
            {
                get
                {
                    if (PercentChange > 0 && PercentChange > PercentChangeError)
                        return false;
                    if (PercentChange < 0 && PercentChange < -PercentChangeError)
                        return true;
                    else
                        return null;
                }
            }
        }

        private static IEnumerable<TestIterationResult> ParseEtlFiles(IEnumerable<string> etlPaths)
        {
            return
                from path in etlPaths.AsParallel()
                from result in ParseOneEtlFile(path)
                select result;
        }

        private static IEnumerable<TestIterationResult> ParseOneEtlFile(string path)
        {
            Console.WriteLine($"Parsing {path}");

            List<TestIterationResult> results = new List<TestIterationResult>();
            using (var source = new ETWTraceEventSource(path))
            {
                if (source.EventsLost > 0)
                    throw new Exception($"Events were lost in trace '{path}'");

                MicrosoftXunitBenchmarkTraceEventParser benchmarkParser = new MicrosoftXunitBenchmarkTraceEventParser(source);

                Dictionary<string, TestIterationResult> currentIterations = new Dictionary<string, TestIterationResult>();

                benchmarkParser.BenchmarkIterationStart += args =>
                {
                    var currentIteration = new TestIterationResult();
                    currentIteration.EtlPath = path;
                    currentIteration.RunId = args.RunId;
                    currentIteration.TestName = args.BenchmarkName;
                    currentIteration.TestIteration = args.Iteration;
                    currentIteration.StartTime = args.TimeStamp;
                    currentIteration.RelativeStartMilliseconds = args.TimeStampRelativeMSec;
                    currentIteration.tempProcessIds.Add(args.ProcessID);

                    currentIterations[args.RunId] = currentIteration;
                };

                benchmarkParser.BenchmarkIterationStop += args =>
                {
                    TestIterationResult currentIteration = currentIterations[args.RunId];
                    currentIteration.RelativeStopMilliseconds = args.TimeStampRelativeMSec;
                    currentIteration.Failed = !args.Success;

                    currentIterations.Remove(args.RunId);
                    currentIteration.tempProcessIds = null;
                    results.Add(currentIteration);
                };

                source.Kernel.ProcessStart += args =>
                {
                    foreach (var currentIteration in currentIterations.Values)
                        if (currentIteration.tempProcessIds.Contains(args.ParentID))
                            currentIteration.tempProcessIds.Add(args.ProcessID);
                };

                source.Kernel.ProcessStop += args =>
                {
                    foreach (var currentIteration in currentIterations.Values)
                        currentIteration.tempProcessIds.Remove(args.ProcessID);
                };

                source.Clr.GCStart += args =>
                {
                    foreach (var currentIteration in currentIterations.Values)
                        if (currentIteration.tempProcessIds.Contains(args.ProcessID))
                            currentIteration.GCCount++;
                };

                source.Process();
            }

            return results;
        }
    }
}
