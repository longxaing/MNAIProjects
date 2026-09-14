import { describe, expect, it } from "vitest";
import { title } from "./title";

describe("title", () => {
  it("is populated", () => expect(title).toBe("Generated App"));
});