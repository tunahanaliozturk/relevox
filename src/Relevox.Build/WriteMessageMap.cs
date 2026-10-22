using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Relevox.Build;

/// <summary>
/// Copies the message map the generator compiled into an assembly out to a file (ADR 0014).
/// </summary>
/// <remarks>
/// The map is the constant <c>RelevoxMessageMap.Json</c>. It is read from metadata, without loading the assembly,
/// so the task never holds a lock on a file the next build has to overwrite. The file is only written when its
/// content changes, so an edit that changes no message leaves it, and its timestamp, alone.
/// </remarks>
public sealed class WriteMessageMap : Task
{
    /// <summary>The assembly the compiler just wrote.</summary>
    [Required]
    public string Assembly { get; set; } = "";

    /// <summary>Where the map goes.</summary>
    [Required]
    public string File { get; set; } = "";

    /// <inheritdoc />
    public override bool Execute()
    {
        string? json = Read(Assembly);
        if (json is null)
        {
            // A project that only uses another project's mediator has no map of its own; the switch may be set
            // for a whole solution in Directory.Build.props.
            Log.LogMessage(MessageImportance.Low, "Relevox: {0} has no message map; nothing written.", Assembly);
            return true;
        }

        if (System.IO.File.Exists(File) && System.IO.File.ReadAllText(File) == json)
        {
            return true;
        }

        string? directory = Path.GetDirectoryName(File);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.IO.File.WriteAllText(File, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Log.LogMessage(MessageImportance.Normal, "Relevox: wrote the message map to {0}.", File);
        return true;
    }

    private static string? Read(string assembly)
    {
        using FileStream stream = System.IO.File.OpenRead(assembly);
        using PEReader image = new(stream);
        MetadataReader metadata = image.GetMetadataReader();
        foreach (TypeDefinitionHandle typeHandle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(typeHandle);
            if (!metadata.StringComparer.Equals(type.Name, "RelevoxMessageMap"))
            {
                continue;
            }

            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                if (metadata.StringComparer.Equals(field.Name, "Json") && !field.GetDefaultValue().IsNil)
                {
                    BlobReader value = metadata.GetBlobReader(metadata.GetConstant(field.GetDefaultValue()).Value);
                    return value.ReadUTF16(value.Length);
                }
            }
        }

        return null;
    }
}
