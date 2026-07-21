using Colossal.Serialization.Entities;
using Unity.Entities;

namespace ConcurrentBusBoarding
{
    public struct BoardingZoneOverride : IComponentData, ISerializable
    {
        public float m_Offset;
        public float m_Length;

        internal BoardingZoneOverride(float offset, float length)
        {
            m_Offset = offset;
            m_Length = length;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Offset);
            writer.Write(m_Length);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Offset);
            reader.Read(out m_Length);
        }
    }
}
