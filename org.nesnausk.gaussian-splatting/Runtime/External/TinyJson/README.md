# Tiny Json

[![Build Status](https://travis-ci.org/zanders3/json.png?branch=master)](https://travis-ci.org/zanders3/json)
[![License](https://img.shields.io/badge/license-MIT-lightgrey.svg)](https://raw.githubusercontent.com/zanders3/json/master/LICENSE)
[![NuGet](https://img.shields.io/nuget/v/TinyJson.svg)](https://www.nuget.org/packages/TinyJson)

A really simple C# JSON parser in ~350 lines
- Attempts to parse JSON files with minimal GC allocation
- Nice and simple `"[1,2,3]".FromJson<List<int>>()` API
- Classes and structs can be parsed too!
```csharp
class Foo 
{ 
  public int Value;
}
"{\"Value\":10}".FromJson<Foo>()
```
- Anonymous JSON is parsed into `Dictionary<string,object>` and `List<object>`
```csharp
var test = "{\"Value\":10}".FromJson<object>();
int number = ((Dictionary<string,object>)test)["Value"];
```
- No JIT Emit support to support AOT compilation on iOS
- Attempts are made to NOT throw an exception if the JSON is corrupted or invalid: returns null instead.
- Only public fields and property setters on classes/structs will be written to
- You can *optionally* use `[IgnoreDataMember]` and `[DataMember(Name="Foo")]` to ignore vars and override the default name

Limitations:
- No JIT Emit support to parse structures quickly
- Limited to parsing <2GB JSON files (due to int.MaxValue)
- Parsing of abstract classes or interfaces is NOT supported and will throw an exception.

## Changelog

- v1.1 Support added for Enums and fixed Unity compilation
- v1.0 Initial Release

## Example Usage

This example will write a list of ints to a File and read it back again:
```csharp
using System;
using System.IO;
using System.Collections.Generic;

using TinyJson;

public static class JsonTest
{
  public static void Main(string[] args)
  {
    //Write a file
    List<int> values = new List<int> { 1, 2, 3, 4, 5, 6 };
    string json = values.ToJson();
    File.WriteAllText("test.json", json);
    
    //Read it back
    string fileJson = File.ReadAllText("test.json");
    List<int> fileValues = fileJson.FromJson<List<int>>();
  }
}
```
Save this as `JsonTest.cs` then compile and run with `mcs JsonTest.cs && mono JsonTest.exe`

## Installation

Simply copy and paste the [JSON Parser](https://raw.githubusercontent.com/zanders3/json/master/src/JSONParser.cs) and/or the [JSON Writer](https://raw.githubusercontent.com/zanders3/json/master/src/JSONWriter.cs) into your project. I also provide NuGet but I recommend the copy paste route ;)
