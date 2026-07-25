using Colossal.Serialization.Entities;
using Unity.Entities;

namespace ConcurrentBusBoarding
{
    public struct BoardingZoneColorOverride : IComponentData, ISerializable
    {
        public bool m_UseLineColor;

        internal BoardingZoneColorOverride(bool useLineColor)
        {
            m_UseLineColor = useLineColor;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_UseLineColor);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_UseLineColor);
        }
    }
}
