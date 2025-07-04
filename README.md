# XTW

[![Downloads](https://img.shields.io/github/downloads/valleyofdoom/xtw/total.svg)](https://github.com/valleyofdoom/xtw/releases)

## Getting Started

The simplest method of getting started is to run the ``xtw_etl_collection.bat``. This will save a report to a file named ``xtw-report.txt``. The output may be extensive horizontally, so make sure to disable word wrap in your text editor.

## Output

|Section|Description|
|---|---|
|Total ISR/DPC Usage by CPU (usecs and count)|For each CPU, the sum of all ISR/DPC elapsed times and occurrences for all that are scheduled that CPU|
|ISR/DPC Interval (ms)|A measure of how frequently ISR/DPCs are scheduled. For example, 1ms is 1000 times per second, 0.125ms is 8000 times per second|
|ISR/DPC Elapsed Times (usecs)|The time taken for ISR/DPCs to execute (end timestamp minus start timestamp)|
|PresentMon|Various metrics such as Frame Time, CPU Busy, CPU Wait, GPU Busy, GPU Wait, Displayed Time, Input-to-Photon Latency, Click-to-Photon Latency. See the [PresentMon docs](https://github.com/GameTechDev/PresentMon/blob/main/README-CaptureApplication.md) for definitions of each metric|

## Command Line Options

|Option|Description|
|---|---|
|``--etl-file <path>``|Analyze an existing trace log|
|``--no-banner``|Hides the startup banner|
|``--symbols``|Support for symbol resolution|
|``--delay <seconds>``|Delay trace log collection for the specified duration|
|``--timed <seconds>``|Collect events for the specified duration and stop automatically|
|``--output-report-file <path>``|Path for report output file.|
|``--merge <trace1> <trace2> ... <merged_trace>``|Merge 2 or more traces. E.g. ``--merge trace1.etl trace2.etl merged.etl``|
