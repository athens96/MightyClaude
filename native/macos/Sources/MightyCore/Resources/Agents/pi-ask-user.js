// Mighty Code's question tool for pi. The app loads it for each run (`pi -e <this file>`);
// nothing is written to ~/.pi. In the app's RPC mode the dialog below reaches the pane as an
// extension_ui_request, is shown as the question card and answered with an
// extension_ui_response: one option, a typed answer, or (multiple) the picks joined by ", ".
export default function (pi) {
  pi.registerTool({
    name: "ask_user",
    label: "Ask user",
    description:
      "Ask the user one question and wait for the answer. Pass 2 to 6 short options for a multiple-choice question, or no options for a free-text answer. The user can always type their own answer instead of picking an option.",
    promptSnippet: "Ask the user a multiple-choice or free-text question and wait for the answer",
    promptGuidelines: [
      "Use ask_user when the user must choose between alternatives or a requirement is unclear, instead of listing the choices as text and ending the turn.",
    ],
    executionMode: "sequential",
    parameters: {
      type: "object",
      properties: {
        question: { type: "string", description: "The question, one or two sentences." },
        options: { type: "array", items: { type: "string" }, description: "The choices, each a short label. Leave out for a free-text answer." },
        multiple: { type: "boolean", description: "True when the user may pick more than one option." },
      },
      required: ["question"],
    },
    async execute(_toolCallId, params, signal, _onUpdate, ctx) {
      const text = (value) => ({ content: [{ type: "text", text: value }], details: {} });
      const question = String(params.question ?? "").trim();
      const options = [...new Set((Array.isArray(params.options) ? params.options : []).map((option) => String(option).trim()).filter(Boolean))];
      if (!question) return text("ask_user needs a question.");
      if (!ctx.hasUI) return text("The question can't be shown here. Ask it in your reply instead.");
      // `multiple` travels in the call's arguments: the app's card reads it there.
      const answer = options.length > 0 ? await ctx.ui.select(question, options, { signal }) : await ctx.ui.input(question, "", { signal });
      if (answer === undefined || String(answer).trim() === "") return text("The user dismissed the question without answering.");
      return text(`The user answered: ${answer}`);
    },
  });
}
