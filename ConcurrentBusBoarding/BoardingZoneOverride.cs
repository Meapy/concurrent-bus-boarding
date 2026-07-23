using Colossal.Serialization.Entities;
using Unity.Entities;
using UnityColor = UnityEngine.Color;

namespace ConcurrentBusBoarding
{
    public struct BoardingZoneOverride : IComponentData, ISerializable
    {
        public float m_Offset;
        public float m_Length;
        public UnityColor m_Color;

        internal BoardingZoneOverride(float offset, float length, UnityColor color = default)
        {
            m_Offset = offset;
            m_Length = length;
            m_Color = color;
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Offset);
            writer.Write(m_Length);
            writer.Write(m_Color.r);
            writer.Write(m_Color.g);
            writer.Write(m_Color.b);
            writer.Write(m_Color.a);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Offset);
            reader.Read(out m_Length);
            reader.Read(out float r);
            reader.Read(out float g);
            reader.Read(out float b);
            reader.Read(out float a);
            m_Color = new UnityColor(r, g, b, a);
        }
    }
}
