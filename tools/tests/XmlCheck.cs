using System;
using System.Xml;

/// <summary>
/// Parses every XML file the mod ships, and fails the build if one will not.
///
/// Besiege says nothing about an XML it cannot read. The block is simply absent
/// from the toolbar, which looks exactly like a mod that failed to load, a module
/// that threw, or a mesh that could not be found, and none of those is where the
/// fault is.
///
/// The trap this exists for: an XML comment may not contain two hyphens in a row.
/// Ordinary prose produces them the moment a dash is written that way, and one is
/// enough to take the block out of the game.
/// </summary>
static class XmlCheck
{
    public static int Main(string[] args)
    {
        int bad = 0;
        for (int i = 0; i < args.Length; i++)
        {
            try
            {
                XmlDocument document = new XmlDocument();
                document.Load(args[i]);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("  " + args[i] + ": " + e.Message);
                bad++;
            }
        }
        if (bad == 0)
        {
            Console.WriteLine("XML check: " + args.Length + " file(s) parse.");
            return 0;
        }
        return 1;
    }
}
