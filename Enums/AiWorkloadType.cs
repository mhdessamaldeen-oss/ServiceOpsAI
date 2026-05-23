namespace ServiceOpsAI.Enums
{
    public enum AiWorkloadType
    {
        Default,
        Analysis,
        Rag,
        Copilot,
        /// <summary>
        /// Intent router. Runs BEFORE the Copilot SQL generator and classifies the question
        /// into one of {SQL, Chat, Tool, OutOfScope, Refinement}. Lets the Copilot model
        /// (which may be a narrow fine-tune) stay a specialist — it only sees questions
        /// already classified as SQL. Without this workload, a narrow fine-tune suffers
        /// catastrophic forgetting on chat/tool/OOS inputs.
        /// </summary>
        Classifier
    }
}