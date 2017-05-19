namespace WebAssembly.Instructions
{
	/// <summary>
	/// Load 1 byte and zero-extend i8 to i64.
	/// </summary>
	public class Int64Load8Unsigned : MemoryImmediateInstruction
	{
		/// <summary>
		/// Always <see cref="OpCode.Int64Load8Unsigned"/>.
		/// </summary>
		public sealed override OpCode OpCode => OpCode.Int64Load8Unsigned;

		/// <summary>
		/// Creates a new  <see cref="Int64Load8Unsigned"/> instance.
		/// </summary>
		public Int64Load8Unsigned()
		{
		}

		internal Int64Load8Unsigned(Reader reader)
			: base(reader)
		{
		}
	}
}