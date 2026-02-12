import { useState } from "react";
import { SubbarContentContext, SubbarSetterContext } from "./subbarContextStore.js";

export function SubbarProvider({ children }) {
  const [content, setContent] = useState(null);

  return (
    <SubbarSetterContext.Provider value={setContent}>
      <SubbarContentContext.Provider value={content}>
        {children}
      </SubbarContentContext.Provider>
    </SubbarSetterContext.Provider>
  );
}
