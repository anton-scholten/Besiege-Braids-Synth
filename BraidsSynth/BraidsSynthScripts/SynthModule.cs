using System.Xml.Serialization;
using Modding.Modules;

namespace BraidsSynth
{
    /// <summary>
    /// The block module: the deserialised &lt;BraidsSynth&gt; element in
    /// SynthBlock.xml. Nothing is configurable from the XML yet -- the models are
    /// code -- but the module has to exist for the block to carry a behaviour.
    ///
    /// Named SynthModule rather than BraidsSynth because that is the namespace's
    /// own name, and a type sharing its namespace's name is the sort of
    /// self-reference Besiege's in-game compiler handles badly.
    ///
    /// System.Xml is on the mod loader's blacklist as *code*; its scanner never
    /// looks at custom attributes, so [Xml*] markers are the supported way to name
    /// what a module deserialises.
    /// </summary>
    [XmlRoot("BraidsSynth")]
    public class SynthModule : BlockModule
    {
    }
}
