// Call Backend API
    async function callAPI(thesis) {
      try {
        const response = await fetch("https://oncologic-unpopularly-shirley.ngrok-free.dev/getArticles", {
          method: "POST",
          headers: { 
            "Content-Type": "application/json",
            "ngrok-skip-browser-warning": "true"
           },

          body: JSON.stringify({"thesis": thesis }),
          redirect: 'follow'
        });

        if (!response.ok) throw new Error(`Server error: ${response.status}`);
        return await response.json();
      } catch (err) {
        console.error("Error:", err);
        alert("Failed to reach backend. Check if ngrok is active and run via http://localhost (not file://)." + err.message);
        return null;
      }
    }