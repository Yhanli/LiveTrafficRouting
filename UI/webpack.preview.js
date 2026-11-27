// Browser preview of the panel (no game needed): npm run preview, then open preview/index.html.
// The game's UI libraries (cs2/api, cs2/ui) are replaced by simple stand-ins in preview/, and React is bundled.
const path = require("path");

module.exports = {
  mode: "development",
  devtool: false,
  entry: { preview: "./preview/preview.tsx" },
  output: { path: path.resolve(__dirname, "preview", "dist"), filename: "[name].js" },
  module: {
    rules: [
      { test: /\.tsx?$/, use: { loader: "ts-loader", options: { transpileOnly: true } }, exclude: /node_modules/ },
      {
        test: /\.s?css$/,
        use: [
          "style-loader",
          { loader: "css-loader", options: { modules: { auto: true, exportLocalsConvention: "camelCase", localIdentName: "[local]_[hash:base64:3]" } } },
          "sass-loader",
        ],
      },
      { test: /\.(png|jpe?g|gif|svg)$/i, type: "asset/inline" },
    ],
  },
  resolve: {
    extensions: [".tsx", ".ts", ".js"],
    modules: ["node_modules", path.join(__dirname, "src")],
    alias: {
      "cs2/api": path.resolve(__dirname, "preview", "mock-api.ts"),
      "cs2/ui": path.resolve(__dirname, "preview", "mock-ui.tsx"),
      "mod.json": path.resolve(__dirname, "mod.json"),
    },
  },
};
