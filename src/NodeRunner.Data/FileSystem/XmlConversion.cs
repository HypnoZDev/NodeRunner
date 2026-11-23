namespace NodeRunner.Data.FileSystem;

// A game file turned into the XML read by Sollumz and CodeWalker ("name.ydr.xml"), with the files that go with it
// (images "name/texture.dds"), by path relative to the XML.
public sealed record XmlConversion(string FileName, string Text, IReadOnlyList<(string Path, byte[] Data)> Files);
